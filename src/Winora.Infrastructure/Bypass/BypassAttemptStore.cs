using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Core.Bypass;
using Winora.Infrastructure.Paths;

namespace Winora.Infrastructure.Bypass;

/// <summary>Where the record of tried strategies lives.</summary>
public interface IBypassAttemptStore
{
    /// <summary>Every attempt kept, newest first. Empty when there is no usable record.</summary>
    IReadOnlyList<BypassAttempt> Read();

    /// <summary>Stores the record. False when it could not be written.</summary>
    bool Write(IReadOnlyList<BypassAttempt> attempts);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Shaped after <c>UserProfileStore</c> and for the same reason: plain JSON moved into place,
/// rather than <c>AtomicJsonFile</c>, which the journal and the backups use. That machinery carries
/// schema versions, digests and an authoritative-versus-projection distinction because losing a
/// journal entry can leave a machine changed with no way back. Losing this file means being asked
/// "did it work?" about a strategy that was already ruled out — an evening's inconvenience, not a
/// broken computer. Borrowing the heavy apparatus would claim otherwise.
/// </para>
/// <para>
/// An unreadable file reads as no history at all, and the search starts from the top. That is the
/// same outcome as a fresh install, which is a state the screen already has to handle correctly.
/// </para>
/// </remarks>
public sealed class BypassAttemptStore : IBypassAttemptStore
{
    private const string FileName = "bypass-attempts.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    private readonly string _directory;

    public BypassAttemptStore()
        : this(WinoraDataPaths.RootForCurrentUser())
    {
    }

    public BypassAttemptStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    private string Path => global::System.IO.Path.Combine(_directory, FileName);

    public IReadOnlyList<BypassAttempt> Read()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return [];
            }

            var stored = JsonSerializer.Deserialize<StoredAttempt[]>(File.ReadAllText(Path), Options);

            if (stored is null)
            {
                return [];
            }

            return stored
                // A row with no strategy names nothing and can never match one, so it is dropped
                // rather than kept as a blank line in the history. The file is editable text.
                .Where(static attempt => !string.IsNullOrWhiteSpace(attempt.StrategyId))
                .Select(static attempt => new BypassAttempt(
                    attempt.StrategyId!.Trim(),
                    attempt.WhenUtc,

                    // A number outside the enum means a file written by a newer build, or edited.
                    // Reading it as "nobody has judged this" keeps the strategy in the search
                    // instead of silently ruling it in or out on a value we do not understand.
                    Enum.IsDefined(attempt.Outcome) ? attempt.Outcome : BypassOutcome.Unknown))
                .OrderByDescending(static attempt => attempt.WhenUtc)
                .Take(BypassAttemptRules.MaxKept)
                .ToArray();
        }
        catch (Exception)
        {
            // Unreadable, half-written, or hand-edited into something that is not JSON. All of it
            // means the same thing here: no history, and the search begins at the top.
            return [];
        }
    }

    public bool Write(IReadOnlyList<BypassAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        var temporary = Path + ".tmp";

        try
        {
            Directory.CreateDirectory(_directory);

            var stored = attempts
                .Select(static attempt => new StoredAttempt
                {
                    StrategyId = attempt.StrategyId,
                    WhenUtc = attempt.WhenUtc,
                    Outcome = attempt.Outcome,
                })
                .ToArray();

            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, Options));

            // Moved rather than written in place, so a reader sees the whole of the old file or the
            // whole of the new one. The app writes this while it is running.
            File.Move(temporary, Path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception)
            {
                // Nothing further to try, and the failure below is the one worth reporting.
            }

            return false;
        }
    }

    private sealed class StoredAttempt
    {
        public string? StrategyId { get; set; }

        public DateTimeOffset WhenUtc { get; set; }

        public BypassOutcome Outcome { get; set; }
    }
}
