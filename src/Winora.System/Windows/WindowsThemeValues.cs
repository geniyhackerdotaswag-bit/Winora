using System.Globalization;

namespace Winora.System.Windows;

/// <summary>
/// The canonical value vocabulary for the Windows appearance.
/// </summary>
/// <remarks>
/// Stable identifiers, not user-facing text: <c>Winora.App</c> renders them through its localized
/// resources, and the action journal stores the identifier. The accent is written as six hex digits
/// with no alpha — the alpha byte belongs to the file being edited and is never part of what the
/// person chose.
/// </remarks>
public static class WindowsThemeValues
{
    public const string Kind = "winora.value.windows-theme";

    private const string Dark = "dark";

    private const string Light = "light";

    /// <summary>
    /// Written by an earlier build, still read.
    /// </summary>
    /// <remarks>
    /// Nothing writes this any more, but change histories on machines that ran that build carry it.
    /// Reading it keeps those rows undoable instead of turning them into a value the program no
    /// longer understands.
    /// </remarks>
    private const string Automatic = "auto";

    /// <summary>
    /// The mode and accent colour, as one stable string.
    /// </summary>
    /// <remarks>
    /// Whether Windows was choosing the accent itself is deliberately not part of this. It cannot
    /// be put back: setting a colour requires <c>AutoColorization=0</c>, and handing the choice
    /// back afterwards needs a second theme, which Windows ignores because applying the first one
    /// left its Settings window open. Recording a fact that undo cannot restore would make every
    /// undo of this change fail its own verification. The colour is recorded either way, so the
    /// desktop goes back to the colour it had.
    /// </remarks>
    public static string For(WindowsThemeSettings settings)
    {
        var mode = settings.Mode == WindowsThemeMode.Light ? Light : Dark;

        return settings.Accent is { } accent
            ? mode + " " + (accent & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture)
            : mode;
    }

    public static bool TryParse(string? text, out WindowsThemeSettings settings)
    {
        settings = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        var mode = parts[0] switch
        {
            Dark => WindowsThemeMode.Dark,
            Light => WindowsThemeMode.Light,
            _ => (WindowsThemeMode?)null,
        };

        if (mode is null)
        {
            return false;
        }

        if (parts.Length == 1)
        {
            settings = new WindowsThemeSettings(mode, null);
            return true;
        }

        var automatic = string.Equals(parts[1], Automatic, StringComparison.Ordinal);

        if (automatic && parts.Length == 2)
        {
            settings = new WindowsThemeSettings(mode, null);
            return true;
        }

        var colour = automatic ? parts[2] : parts[1];

        if ((!automatic && parts.Length != 2) ||
            colour.Length != 6 ||
            !uint.TryParse(colour, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var accent))
        {
            return false;
        }

        // The flag is not carried through: it is what an older build recorded, and it is not
        // something this can put back.
        settings = new WindowsThemeSettings(mode, (int)accent);
        return true;
    }
}
