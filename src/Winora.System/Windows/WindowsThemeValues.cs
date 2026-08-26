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

    /// <summary>Windows picks the accent from the wallpaper, so there is no colour to name.</summary>
    private const string Automatic = "auto";

    public static string For(WindowsThemeSettings settings)
    {
        var mode = settings.Mode == WindowsThemeMode.Light ? Light : Dark;

        if (settings.IsAccentAutomatic)
        {
            return mode + " " + Automatic;
        }

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

        if (parts.Length is < 1 or > 2)
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

        if (string.Equals(parts[1], Automatic, StringComparison.Ordinal))
        {
            settings = new WindowsThemeSettings(mode, null, IsAccentAutomatic: true);
            return true;
        }

        if (parts[1].Length != 6 ||
            !uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var accent))
        {
            return false;
        }

        settings = new WindowsThemeSettings(mode, (int)accent);
        return true;
    }
}
