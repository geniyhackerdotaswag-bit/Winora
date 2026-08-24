using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Core.Profile;
using Winora.Infrastructure.Paths;

namespace Winora.Infrastructure.Profile;

/// <summary>Where the four fields live.</summary>
public interface IUserProfileStore
{
    /// <summary>The stored profile, or null when there is not a usable one.</summary>
    UserProfile? Read();

    /// <summary>Stores the profile. False when it could not be written.</summary>
    bool Write(UserProfile profile);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Plain JSON written to a temporary file and moved into place, rather than
/// <c>AtomicJsonFile</c>, which the rest of the state uses. That type carries schema versions,
/// digests and an authoritative-versus-projection distinction, all of which exist because losing
/// the journal or a backup record would leave a machine changed with no way back. Losing a name
/// and an avatar means being asked for them again. Borrowing that machinery here would suggest
/// this file matters as much as those, and it does not.
/// </para>
/// <para>
/// The move is still atomic: a reader sees either the old file or the new one, never a half-written
/// one. That much is worth having for a file written while the app is running.
/// </para>
/// </remarks>
public sealed class UserProfileStore : IUserProfileStore
{
    private const string FileName = "profile.json";

    /// <summary>
    /// The current shape of profile.json.
    /// </summary>
    /// <remarks>
    /// Version 2 added the password. A file without one was written before registration existed,
    /// and is treated as no profile at all rather than as a profile to be trusted — see Read.
    /// </remarks>
    private const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    private readonly string _directory;

    public UserProfileStore()
        : this(WinoraDataPaths.RootForCurrentUser())
    {
    }

    public UserProfileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    private string Path => global::System.IO.Path.Combine(_directory, FileName);

    public UserProfile? Read()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            var stored = JsonSerializer.Deserialize<StoredProfile>(File.ReadAllText(Path), Options);

            if (stored is null)
            {
                return null;
            }

            var normalisedName = ProfileRules.NormaliseName(stored.Name);

            // A profile with no name has nothing for the card to show and nothing for the initial
            // to take, so it is not one. The welcome window asks again.
            if (string.IsNullOrEmpty(normalisedName))
            {
                return null;
            }

            // If the name is too long but present, truncate it to fit rather than discard
            // the entire profile. The stored name may come from a hand edit or a looser build.
            var finalName = normalisedName.Length > ProfileRules.NameMaxLength
                ? normalisedName.Substring(0, ProfileRules.NameMaxLength)
                : normalisedName;

            // A profile with no usable digest never went through registration. That is the only
            // way in now, so such a file reads as absent and the person is asked to register.
            var digest = new PasswordDigest(
                stored.PasswordHash ?? string.Empty,
                stored.PasswordSalt ?? string.Empty,
                stored.PasswordIterations);

            if (stored.SchemaVersion < CurrentSchemaVersion ||
                digest.Hash.Length == 0 ||
                digest.Salt.Length == 0 ||
                digest.Iterations <= 0)
            {
                return null;
            }

            return new UserProfile(
                finalName,
                stored.Email?.Trim() ?? string.Empty,
                stored.Avatar,
                stored.CreatedUtc,
                digest);
        }
        catch (Exception)
        {
            // Unreadable, half-written, or edited by hand into something that is not JSON. All of
            // it means the same thing to everybody upstream: there is no profile yet.
            return null;
        }
    }

    public bool Write(UserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            var temporary = Path + ".tmp";

            Directory.CreateDirectory(_directory);

            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    new StoredProfile(
                        CurrentSchemaVersion,
                        profile.Name,
                        profile.Email,
                        profile.Avatar,
                        profile.CreatedUtc,
                        profile.Password?.Hash ?? string.Empty,
                        profile.Password?.Salt ?? string.Empty,
                        profile.Password?.Iterations ?? 0),
                    Options));

            File.Move(temporary, Path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            TryDelete(Path + ".tmp");
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Nothing depends on it going, and nothing here is worth an exception.
        }
    }

    private sealed record StoredProfile(
        int SchemaVersion,
        string? Name,
        string? Email,
        int Avatar,
        DateTimeOffset CreatedUtc,
        string? PasswordHash,
        string? PasswordSalt,
        int PasswordIterations);
}
