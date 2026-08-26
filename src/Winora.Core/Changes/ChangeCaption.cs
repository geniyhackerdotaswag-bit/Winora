using System.Text;

namespace Winora.Core.Changes;

/// <summary>
/// Turns what the journal stored into what a person can read.
/// </summary>
/// <remarks>
/// <para>
/// The change history is the screen that has to be believed: it is the proof that anything Winora
/// did can be taken back. Until 2026-08-26 it read
/// <c>MicrosoftEdgeAutoLaunch_48EDF2D71EE0AC2F5D41DCF1908B2D8F — enabled → disabled</c>. That is
/// the registry value's own name and the two raw states, printed as stored. A screen meant to
/// reassure was showing its own plumbing.
/// </para>
/// <para>
/// Nothing here invents. A name this does not recognise comes back exactly as it went in, because a
/// raw name is honest and a guessed one is not — and on a screen about undoing changes, a wrong
/// name is worse than an ugly one.
/// </para>
/// </remarks>
public static class ChangeCaption
{
    /// <summary>
    /// What Windows suffixes an auto-start value with: the word, then a per-install identifier.
    /// </summary>
    /// <remarks>
    /// Browsers and Electron applications register themselves as
    /// <c>&lt;Product&gt;AutoLaunch_&lt;32 hex digits&gt;</c>. The identifier differs on every
    /// machine, so it is meaningless to the person reading and merely long.
    /// </remarks>
    private const string AutoLaunchMarker = "AutoLaunch_";

    /// <summary>The arrow the journal writes between the old value and the new one.</summary>
    private const string Arrow = "→";

    /// <summary>The stored title, made readable. Unrecognised titles come back unchanged.</summary>
    public static string Readable(string? title)
    {
        var trimmed = (title ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var marker = trimmed.IndexOf(AutoLaunchMarker, StringComparison.Ordinal);

        if (marker <= 0)
        {
            return trimmed;
        }

        // Everything after the marker is the per-install identifier. It is dropped whether or not
        // it looks like hex: nothing after that word has ever been for reading.
        return Spaced(trimmed[..marker]);
    }

    /// <summary>The value a change moved away from, or empty when the summary holds no arrow.</summary>
    public static string Before(string? summary) => Side(summary, 0);

    /// <summary>The value a change moved to, or empty when the summary holds no arrow.</summary>
    public static string After(string? summary) => Side(summary, 1);

    private static string Side(string? summary, int index)
    {
        var parts = (summary ?? string.Empty).Split(Arrow, StringSplitOptions.TrimEntries);

        return parts.Length == 2 ? parts[index] : string.Empty;
    }

    /// <summary>
    /// Splits a run-together name at its capitals: <c>MicrosoftEdge</c> becomes
    /// <c>Microsoft Edge</c>.
    /// </summary>
    /// <remarks>
    /// A run of capitals is left whole, so <c>EADM</c> does not become <c>E A D M</c>, and a capital
    /// that ends such a run starts the next word — <c>PoEOverlay</c> gives <c>PoE Overlay</c>.
    /// </remarks>
    private static string Spaced(string name)
    {
        var text = new StringBuilder(name.Length + 8);

        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];

            var startsWord =
                index > 0 &&
                char.IsUpper(current) &&
                (!char.IsUpper(name[index - 1]) ||
                    (index + 1 < name.Length && char.IsLower(name[index + 1])));

            if (startsWord && text.Length > 0 && text[^1] != ' ')
            {
                text.Append(' ');
            }

            text.Append(current);
        }

        return text.ToString().Trim();
    }
}
