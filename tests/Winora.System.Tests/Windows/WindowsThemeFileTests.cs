using System.Text;
using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Windows;

/// <summary>
/// Editing a Windows theme file.
/// </summary>
/// <remarks>
/// Everything here is byte work, and the reason is in the file it edits: a theme carries a display
/// name in whatever language its author used, and the one on the owner's machine turned out to be
/// single-byte rather than the UTF-16 the format is usually assumed to be. Decoding the whole file
/// to change two lines would put that name through a guess.
/// </remarks>
public sealed class WindowsThemeFileTests
{
    /// <summary>The shape of the real thing, cut down to the parts this touches.</summary>
    private const string Sample =
        "; Copyright Microsoft Corp.\r\n" +
        "\r\n" +
        "[Theme]\r\n" +
        // Bytes above 127 on purpose: an ASCII name would prove nothing here.
        "DisplayName=\u00CC\u00EE\u00FF \u00F2\u00E5\u00EC\u00E0\r\n" +
        "\r\n" +
        "[Control Panel\\Desktop]\r\n" +
        "Wallpaper=%USERPROFILE%\\AppData\\Local\\Microsoft\\Windows\\Themes\\Pantone.jpg\r\n" +
        "\r\n" +
        "[VisualStyles]\r\n" +
        "Path=%SystemRoot%\\resources\\themes\\Aero\\Aero.msstyles\r\n" +
        "ColorizationColor=0X1F2356\r\n" +
        "SystemMode=Dark\r\n" +
        "AppMode=Dark\r\n";

    // Latin-1 rather than the file's real codepage: it is built into .NET, and it maps every
    // byte 0-255 to one character and back. That is exactly what a byte-transparent test needs,
    // and it keeps a codepage provider out of the test project.
    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);

    private static string Text(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    [Fact]
    public void What_the_file_says_is_read_back()
    {
        var settings = WindowsThemeFile.Read(Bytes(Sample));

        Assert.Equal(WindowsThemeMode.Dark, settings.Mode);
        Assert.Equal(0x1F2356, settings.Accent);
    }

    [Fact]
    public void A_file_with_no_visual_styles_says_nothing_rather_than_guessing()
    {
        var settings = WindowsThemeFile.Read(Bytes("[Theme]\r\nDisplayName=none\r\n"));

        Assert.Null(settings.Mode);
        Assert.Null(settings.Accent);
    }

    [Theory]
    [InlineData(WindowsThemeMode.Light, "Light")]
    [InlineData(WindowsThemeMode.Dark, "Dark")]
    public void Both_mode_lines_are_written_together(WindowsThemeMode mode, string expected)
    {
        var result = Text(WindowsThemeFile.With(Bytes(Sample), mode, accent: null));

        Assert.Contains($"SystemMode={expected}\r\n", result, StringComparison.Ordinal);
        Assert.Contains($"AppMode={expected}\r\n", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Windows will happily hold a system mode and an application mode that disagree. A program
    /// that set one and left the other would leave a half-lit desktop nobody asked for.
    /// </summary>
    [Fact]
    public void A_file_that_disagrees_with_itself_is_made_to_agree()
    {
        var mixed = Sample.Replace("AppMode=Dark", "AppMode=Light", StringComparison.Ordinal);

        var result = Text(WindowsThemeFile.With(Bytes(mixed), WindowsThemeMode.Dark, accent: null));

        Assert.Contains("SystemMode=Dark\r\n", result, StringComparison.Ordinal);
        Assert.Contains("AppMode=Dark\r\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Light", result, StringComparison.Ordinal);
    }

    [Fact]
    public void The_accent_is_written_in_the_form_the_file_uses()
    {
        var result = Text(WindowsThemeFile.With(Bytes(Sample), WindowsThemeMode.Dark, 0x0A1B2C));

        Assert.Contains("ColorizationColor=0X0A1B2C\r\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaving_the_accent_out_leaves_the_line_alone()
    {
        var result = Text(WindowsThemeFile.With(Bytes(Sample), WindowsThemeMode.Light, accent: null));

        Assert.Contains("ColorizationColor=0X1F2356\r\n", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one thing that must survive untouched: everything the file says about wallpaper, sounds
    /// and cursors. Changing the colours must not quietly change the desktop picture.
    /// </summary>
    [Fact]
    public void Every_other_line_comes_through_byte_for_byte()
    {
        var original = Bytes(Sample);
        var result = WindowsThemeFile.With(original, WindowsThemeMode.Light, 0x102030);

        var before = Text(original).Split("\r\n");
        var after = Text(result).Split("\r\n");

        Assert.Equal(before.Length, after.Length);

        for (var line = 0; line < before.Length; line++)
        {
            if (before[line].StartsWith("SystemMode=", StringComparison.Ordinal) ||
                before[line].StartsWith("AppMode=", StringComparison.Ordinal) ||
                before[line].StartsWith("ColorizationColor=", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Equal(before[line], after[line]);
        }
    }

    /// <summary>A name in another language survives, which decoding the file would not guarantee.</summary>
    [Fact]
    public void The_display_name_is_not_re_encoded()
    {
        var original = Bytes(Sample);
        var result = WindowsThemeFile.With(original, WindowsThemeMode.Light, accent: null);

        var nameStart = Array.IndexOf(original, (byte)'=', Text(original).IndexOf("DisplayName", StringComparison.Ordinal));
        var slice = original.AsSpan(nameStart, 12).ToArray();

        Assert.Contains(Convert.ToHexString(slice), Convert.ToHexString(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// A key is only a key at the start of a line. Without that test the editor would find
    /// "AppMode=" inside a longer key and rewrite the wrong place — silently, because the result is
    /// still a file Windows will read.
    /// </summary>
    [Fact]
    public void A_key_that_only_ends_with_the_name_is_not_the_one()
    {
        const string tricky =
            "[VisualStyles]\r\n" +
            "CustomAppMode=Dark\r\n" +
            "AppMode=Dark\r\n";

        var result = Text(WindowsThemeFile.With(Bytes(tricky), WindowsThemeMode.Light, accent: null));

        Assert.Contains("CustomAppMode=Dark\r\n", result, StringComparison.Ordinal);
        Assert.Contains("AppMode=Light\r\n", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Measured on the owner's machine, 2026-08-27: Explorer held 0xFF56231F beside the theme's
    /// 0X1F2356. The same colour, bytes the other way round.
    /// </summary>
    [Fact]
    public void The_accent_converts_between_the_two_forms_Windows_uses()
    {
        Assert.Equal(0x1F2356, WindowsThemeFile.AccentFromExplorer(0xFF56231F));
        Assert.Equal(0xFF56231Fu, WindowsThemeFile.AccentToExplorer(0x1F2356));
    }

    [Theory]
    [InlineData(0x000000)]
    [InlineData(0xFFFFFF)]
    [InlineData(0x1F2356)]
    [InlineData(0xC0FFEE)]
    public void A_colour_survives_the_round_trip(int rrggbb)
    {
        Assert.Equal(rrggbb, WindowsThemeFile.AccentFromExplorer(WindowsThemeFile.AccentToExplorer(rrggbb)));
    }
}
