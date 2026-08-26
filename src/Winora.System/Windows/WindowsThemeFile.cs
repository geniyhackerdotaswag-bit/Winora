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
public readonly record struct WindowsThemeSettings(WindowsThemeMode? Mode, int? Accent);

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

        return new WindowsThemeSettings(mode, accent);
    }

    /// <summary>
    /// Returns the file with the mode and accent replaced.
    /// </summary>
    /// <param name="theme">The file as read from disk.</param>
    /// <param name="mode">The mode to write into both mode lines.</param>
    /// <param name="accent">The accent as 0xRRGGBB, or null to leave whatever is there.</param>
    /// <remarks>
    /// A file with no <c>[VisualStyles]</c> line for one of these is returned without it: Winora
    /// does not add keys to a file Windows wrote, because a key in the wrong section is a theme
    /// Windows may refuse, and refusing is the one outcome this cannot detect afterwards.
    /// </remarks>
    public static byte[] With(ReadOnlySpan<byte> theme, WindowsThemeMode mode, int? accent)
    {
        var text = mode == WindowsThemeMode.Dark ? "Dark"u8.ToArray() : "Light"u8.ToArray();

        var result = Replace(theme, SystemMode, text);
        result = Replace(result, AppMode, text);

        if (accent is { } colour)
        {
            var written = Encoding.ASCII.GetBytes(
                "0X" + (colour & 0xFFFFFF).ToString("X6", global::System.Globalization.CultureInfo.InvariantCulture));

            result = Replace(result, Colorization, written);
        }

        return result;
    }

    /// <summary>
    /// Turns the accent Explorer stores into the one a theme file wants.
    /// </summary>
    /// <remarks>
    /// <c>AccentColorMenu</c> is <c>0xAABBGGRR</c> and a theme's <c>ColorizationColor</c> is
    /// <c>0xRRGGBB</c> — the same colour with its bytes the other way round. Measured rather than
    /// assumed: on 2026-08-27 the owner's machine held <c>AccentColorMenu=0xFF56231F</c> beside
    /// <c>ColorizationColor=0X1F2356</c>.
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

    private static bool TryParseColour(string text, out int value)
    {
        value = 0;
        var trimmed = text.Trim();

        if (trimmed.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return int.TryParse(
            trimmed,
            global::System.Globalization.NumberStyles.HexNumber,
            global::System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }
}
