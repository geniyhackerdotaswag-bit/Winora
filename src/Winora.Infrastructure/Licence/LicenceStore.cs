using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Core.Licence;
using Winora.Infrastructure.Paths;

namespace Winora.Infrastructure.Licence;

/// <summary>What this machine remembers about its subscription between runs.</summary>
public interface ILicenceStore
{
    /// <summary>The stored token, or empty when no key has been entered here.</summary>
    string Token { get; }

    /// <summary>The last thing the site said, or <see cref="LicenceState.None"/>.</summary>
    LicenceState Read();

    /// <summary>Keeps a token and the state it came with.</summary>
    bool Write(string token, LicenceState state);

    /// <summary>Forgets the key on this machine. The subscription itself is untouched.</summary>
    bool Clear();
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// One small file beside everything else Winora keeps, which since 2026-09-03 means beside the
/// program itself. Carry the folder to another machine and the subscription goes with it — which is
/// correct, because the key is the person's, not the computer's, and the site counts machines by
/// the tokens it issued rather than by anything it reads from here.
/// </para>
/// <para>
/// The token is stored as the site gave it, not hashed. Hashing would be theatre: the program has
/// to send this exact string on every check, so it must be able to read it back. What protects it
/// is that it is a token and not the key — it buys nothing on the site, cannot be shown to a person,
/// and the owner can drop every one of them from the cabinet with one button.
/// </para>
/// </remarks>
public sealed class LicenceStore : ILicenceStore
{
    private const string FileName = "licence.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    private readonly string _directory;

    public LicenceStore()
        : this(WinoraDataPaths.RootForCurrentUser())
    {
    }

    public LicenceStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    private string Path => global::System.IO.Path.Combine(_directory, FileName);

    public string Token => ReadStored()?.Token ?? string.Empty;

    public LicenceState Read()
    {
        var stored = ReadStored();

        if (stored is null || stored.ExpiresUtc is null)
        {
            return LicenceState.None;
        }

        return new LicenceState(
            stored.Plan ?? string.Empty,
            stored.ExpiresUtc,
            stored.Machine,
            stored.CheckedUtc ?? DateTimeOffset.MinValue);
    }

    public bool Write(string token, LicenceState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return Save(new Stored
        {
            Token = token ?? string.Empty,
            Plan = state.Plan,
            ExpiresUtc = state.ExpiresUtc,
            Machine = state.Machine,
            CheckedUtc = state.CheckedUtc,
        });
    }

    /// <remarks>
    /// The file is deleted rather than blanked. A file full of empty fields reads, to anybody
    /// looking at the folder, like a subscription that broke; an absent one reads like what it is.
    /// </remarks>
    public bool Clear()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private Stored? ReadStored()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(Path), Options)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file is the same as no file: the program asks for the key again. Throwing
            // here would take down the screen that exists to fix exactly this.
            return null;
        }
    }

    private bool Save(Stored stored)
    {
        var temporary = Path + ".tmp";

        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, Options));

            // Moved rather than written in place, so a reader sees the whole of the old file or the
            // whole of the new one — never half of either.
            File.Move(temporary, Path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception)
            {
                // Already failing; a leftover temporary file is the smaller problem.
            }

            return false;
        }
    }

    private sealed class Stored
    {
        public string Token { get; set; } = string.Empty;

        public string? Plan { get; set; }

        public DateTimeOffset? ExpiresUtc { get; set; }

        public string? Machine { get; set; }

        public DateTimeOffset? CheckedUtc { get; set; }
    }
}
