namespace Winora.Core.Settings;

/// <param name="OperationId">Which setting this is, in the same identifier every screen uses.</param>
/// <param name="Value">What it was set to, as the operation's own text.</param>
public sealed record SettingsEntry(string OperationId, string Value);

/// <summary>Why an entry from a file was not applied.</summary>
public enum SettingsRejection
{
    /// <summary>Accepted.</summary>
    None,

    /// <summary>No such setting in this build. A newer Winora wrote it, or a hand edit invented it.</summary>
    Unknown,

    /// <summary>The file names the same setting twice and does not say which one it means.</summary>
    Duplicated,

    /// <summary>The identifier is empty or is not shaped like one.</summary>
    Malformed,
}

/// <param name="Entry">The entry as it was read.</param>
/// <param name="Rejection">Why it was turned away, or <see cref="SettingsRejection.None"/>.</param>
public sealed record SettingsCandidate(SettingsEntry Entry, SettingsRejection Rejection)
{
    public bool IsAccepted => Rejection == SettingsRejection.None;
}

/// <summary>
/// What a settings file may contain, and what is done with what it does contain.
/// </summary>
/// <remarks>
/// <para>
/// A file is carried between machines by whoever owns it — a cloud folder, a flash drive, a message
/// to oneself. It is therefore text somebody else could have written, and it is read as a proposal
/// rather than as instructions: every entry is checked against the settings this build actually
/// knows, and anything else is reported to the person instead of being applied or silently dropped.
/// </para>
/// <para>
/// Nothing here writes anything. It decides what a file is asking for; the change pipeline decides
/// whether to do it, with the same plan, backup and undo as a click on a screen.
/// </para>
/// </remarks>
public static class SettingsBundle
{
    /// <summary>The shape this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Sorts a file's entries into the ones this build can act on and the ones it cannot.
    /// </summary>
    /// <param name="entries">What the file held, in the order it held them.</param>
    /// <param name="known">Every setting identifier this build understands.</param>
    /// <remarks>
    /// Order is kept. A person comparing the file with the screen should find the same rows in the
    /// same places, including the refused ones — a list that quietly reordered itself would make
    /// the two impossible to hold side by side.
    /// </remarks>
    public static IReadOnlyList<SettingsCandidate> Examine(
        IEnumerable<SettingsEntry> entries,
        IReadOnlySet<string> known)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(known);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var examined = new List<SettingsCandidate>();

        foreach (var entry in entries)
        {
            examined.Add(new SettingsCandidate(entry, RejectionFor(entry, known, seen)));
        }

        return examined;
    }

    private static SettingsRejection RejectionFor(
        SettingsEntry entry,
        IReadOnlySet<string> known,
        HashSet<string> seen)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.OperationId) || entry.Value is null)
        {
            return SettingsRejection.Malformed;
        }

        // Duplicates are refused rather than resolved. Taking the last would apply a value the
        // person may never have seen, and taking the first would ignore one they may have meant;
        // there is no reading of "twice, differently" that is safe to guess at.
        if (!seen.Add(entry.OperationId))
        {
            return SettingsRejection.Duplicated;
        }

        return known.Contains(entry.OperationId)
            ? SettingsRejection.None
            : SettingsRejection.Unknown;
    }
}
