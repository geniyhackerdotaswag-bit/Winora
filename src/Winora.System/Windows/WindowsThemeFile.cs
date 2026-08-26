using System.Text;

namespace Winora.System.Windows;

/// <summary>Whether Windows draws itself light or dark.</summary>
public enum WindowsThemeMode
{
    Light,
    Dark,
}

/// <summary>What a theme file says about mode and accent.</summary>
/// <param name="Mode">The value shared by <c>SystemMode</c> and <c>AppMode</c>, or null when absent.</param>
/// <param name="Accent">The accent as 0xRRGGBB, or null when the file does not set one.</param>
/// <param name="IsAccentAutomatic">
/// <c>AutoColorization=1</c>: Windows picks the accent from the wallpaper and ignores
/// <c>ColorizationColor</c> entirely. Measured on 2026-08-27 — a file carrying both a colour and
/// this flag applies the wallpaper's colour, not the one written down.
/// </param>
public readonly record struct WindowsThemeSettings(
    WindowsThemeMode? Mode,
    int? Accent,
    bool IsAccentAutomatic = false);

/// <summary>
/// Edits a Windows <c>.theme</c> file in place, changing only what was asked for.
/// </summary>
/// <remarks>
/// <para>
/// A theme file is Windows' own format and its own documented way of carrying appearance: the files
/// it ships with live in <c>%SystemRoot%\Resources\Themes</c>, and the extension is registered to
/// <c>themecpl.dll</c>. Winora writes one rather than setting the registry values behind it,
/// because those are not documented as writable and this program changes only what is.
/// </para>
/// <para>
/// Everything here works on bytes. A theme file carries a display name that may be in any language,
/// and decoding it to text and back would re-encode that name through whatever the process happens
/// to think the file's encoding is. Measured on 2026-08-27: the file on the owner's machine is
/// single-byte, not the UTF-16 the format is often assumed to be. Touching only the lines being
/// changed sidesteps the question entirely.
/// </para>
/// <para>
/// The two mode lines are always written together. Windows allows a system and an application mode
/// that disagree, and a program that set one and left the other would produce a half-lit desktop
/// nobody asked for.
/// </para>
/// </remarks>
public static class WindowsThemeFile
{
    private static readonly byte[] SystemMode = "SystemMode="u8.ToArray();
    private static readonly byte[] AppMode = "AppMode="u8.ToArray();
    private static readonly byte[] Colorization = "ColorizationColor="u8.ToArray();
    private static readonly byte[] AutoColorization = "AutoColorization="u8.ToArray();

    private static readonly global::System.Globalization.CultureInfo Invariant =
        global::System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>Reads what a theme file says, without changing it.</summary>
    public static WindowsThemeSettings Read(ReadOnlySpan<byte> theme)
    {
        var mode = ValueOf(theme, SystemMode) is { } text
            ? text.Equals("Dark", StringComparison.OrdinalIgnoreCase) ? WindowsThemeMode.Dark
                : text.Equals("Light", StringComparison.OrdinalIgnoreCase) ? WindowsThemeMode.Light
                : (WindowsThemeMode?)null
            : null;

        int? accent = null;

        if (ValueOf(theme, Colorization) is { } colour &&
            TryParseColour(colour, out var parsed))
        {
            accent = parsed;
        }

        var automatic = ValueOf(theme, AutoColorization) is { } flag &&
            flag.Equals("1", StringComparison.Ordinal);

        return new WindowsThemeSettings(mode, accent, automatic);
    }

    /// <summary>
    /// Returns the file with the wanted appearance written into it.
    /// </summary>
    /// <param name="theme">The file as read from disk.</param>
    /// <param name="wanted">The mode and accent to write. A null mode leaves both mode lines alone.</param>
    /// <remarks>
    /// <para>
    /// A file with no <c>[VisualStyles]</c> line for one of these is returned without it: Winora
    /// does not add keys to a file Windows wrote, because a key in the wrong section is a theme
    /// Windows may refuse, and refusing is the one outcome this cannot detect afterwards.
    /// </para>
    /// <para>
    /// Asking for an automatic accent writes the flag and leaves the colour alone, which is how an
    /// undo puts back a machine that was picking its accent from the wallpaper. Without that, every
    /// undo would silently leave the setting switched off — a change to something the person had
    /// chosen, made by an action whose whole purpose was to change nothing.
    /// </para>
    /// </remarks>
    public static byte[] With(ReadOnlySpan<byte> theme, WindowsThemeSettings wanted)
    {
        var result = theme.ToArray();

        if (wanted.Mode is { } mode)
        {
            var text = mode == WindowsThemeMode.Dark ? "Dark"u8.ToArray() : "Light"u8.ToArray();

            result = Replace(result, SystemMode, text);
            result = Replace(result, AppMode, text);
        }

        if (wanted.IsAccentAutomatic)
        {
            return Replace(result, AutoColorization, "1"u8.ToArray());
        }

        if (wanted.Accent is { } colour)
        {
            // The alpha byte carries the glass opacity and belongs to the user, not to this change.
            // Writing six digits over an eight-digit value would zero it, which is a different
            // desktop from the one they had — and one nothing here asked to change.
            var rgb = (uint)(colour & 0xFFFFFF);
            var written = Encoding.ASCII.GetBytes(AlphaOf(theme) is { } alpha
                ? "0X" + (((uint)alpha << 24) | rgb).ToString("X8", Invariant)
                : "0X" + rgb.ToString("X6", Invariant));

            result = Replace(result, Colorization, written);

            // Without this the colour is written and then ignored: with AutoColorization=1 Windows
            // takes the accent from the wallpaper. The value was 1 on the owner's own machine, so
            // leaving it alone would have made the feature do nothing for the person who asked for it.
            result = Replace(result, AutoColorization, "0"u8.ToArray());
        }

        return result;
    }

    /// <summary>
    /// Turns the accent Windows stores into the one a theme file wants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value to read and write back is <c>HKCU\Software\Microsoft\Windows\DWM\AccentColor</c>,
    /// which is <c>0xAABBGGRR</c>, against a theme's <c>ColorizationColor</c> in <c>0xAARRGGBB</c> —
    /// the same colour with its three colour bytes the other way round.
    /// </para>
    /// <para>
    /// Not <c>AccentColorMenu</c>. That one is a shade Windows derives, and on the owner's machine it
    /// did not match the base accent even before anything was changed. An earlier note in this file
    /// claimed it was the pair to use, on a single reading where the two happened to agree.
    /// </para>
    /// <para>
    /// Settled by experiment on 2026-08-27 rather than by reading bytes and guessing: a theme
    /// carrying <c>ColorizationColor=0XC410FF10</c> produced <c>AccentColor=0xFF10FF10</c>, and
    /// <c>DwmGetColorizationColor</c> — documented to return <c>0xAARRGGBB</c> — confirmed the
    /// direction on the colour that was already there.
    /// </para>
    /// </remarks>
    public static int AccentFromExplorer(uint accentColorMenu)
    {
        var r = (int)(accentColorMenu & 0xFF);
        var g = (int)((accentColorMenu >> 8) & 0xFF);
        var b = (int)((accentColorMenu >> 16) & 0xFF);

        return (r << 16) | (g << 8) | b;
    }

    /// <inheritdoc cref="AccentFromExplorer" />
    public static uint AccentToExplorer(int rrggbb)
    {
        var r = (uint)((rrggbb >> 16) & 0xFF);
        var g = (uint)((rrggbb >> 8) & 0xFF);
        var b = (uint)(rrggbb & 0xFF);

        return 0xFF000000u | (b << 16) | (g << 8) | r;
    }

    /// <summary>The alpha byte of the existing colour, or null when it carries none.</summary>
    private static byte? AlphaOf(ReadOnlySpan<byte> theme)
    {
        if (ValueOf(theme, Colorization) is not { } text)
        {
            return null;
        }

        var digits = text.Trim();
        if (digits.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        return digits.Length == 8 &&
            uint.TryParse(digits, global::System.Globalization.NumberStyles.HexNumber, Invariant, out var raw)
                ? (byte)(raw >> 24)
                : null;
    }

    private static byte[] Replace(ReadOnlySpan<byte> theme, byte[] key, byte[] value)
    {
        var start = IndexOfKey(theme, key);

        if (start < 0)
        {
            return theme.ToArray();
        }

        var from = start + key.Length;
        var to = from;

        while (to < theme.Length && theme[to] != (byte)'\r' && theme[to] != (byte)'\n')
        {
            to++;
        }

        var result = new byte[theme.Length - (to - from) + value.Length];
        theme[..from].CopyTo(result);
        value.CopyTo(result, from);
        theme[to..].CopyTo(result.AsSpan(from + value.Length));

        return result;
    }

    private static string? ValueOf(ReadOnlySpan<byte> theme, byte[] key)
    {
        var start = IndexOfKey(theme, key);

        if (start < 0)
        {
            return null;
        }

        var from = start + key.Length;
        var to = from;

        while (to < theme.Length && theme[to] != (byte)'\r' && theme[to] != (byte)'\n')
        {
            to++;
        }

        return Encoding.ASCII.GetString(theme[from..to]).Trim();
    }

    /// <summary>
    /// Finds a key only where it starts a line.
    /// </summary>
    /// <remarks>
    /// Without the line test, "AppMode=" would also be found inside a longer key that happens to end
    /// with it, and the file would be edited in the wrong place — silently, because the result is
    /// still a file Windows will read.
    /// </remarks>
    private static int IndexOfKey(ReadOnlySpan<byte> theme, byte[] key)
    {
        for (var index = 0; index + key.Length <= theme.Length; index++)
        {
            if (index > 0 && theme[index - 1] != (byte)'\n' && theme[index - 1] != (byte)'\r')
            {
                continue;
            }

            if (theme.Slice(index, key.Length).SequenceEqual(key))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads a theme colour as 0xRRGGBB, dropping the alpha byte.
    /// </summary>
    /// <remarks>
    /// Parsed unsigned. An eight-digit value such as <c>0XC4533222</c> — which is what Windows
    /// actually writes — does not fit a signed int, and parsing it as one yielded a negative
    /// number that then compared unequal to the very colour it came from.
    /// </remarks>
    private static bool TryParseColour(string text, out int value)
    {
        value = 0;
        var trimmed = text.Trim();

        if (trimmed.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        if (!uint.TryParse(
            trimmed,
            global::System.Globalization.NumberStyles.HexNumber,
            Invariant,
            out var raw))
        {
            return false;
        }

        value = (int)(raw & 0xFFFFFF);
        return true;
    }
}
