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
        var result = Text(WindowsThemeFile.With(Bytes(Sample), new WindowsThemeSettings(mode, Accent: null)));

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

        var result = Text(WindowsThemeFile.With(Bytes(mixed), new WindowsThemeSettings(WindowsThemeMode.Dark, Accent: null)));

        Assert.Contains("SystemMode=Dark\r\n", result, StringComparison.Ordinal);
        Assert.Contains("AppMode=Dark\r\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Light", result, StringComparison.Ordinal);
    }

    [Fact]
    public void The_accent_is_written_in_the_form_the_file_uses()
    {
        var result = Text(WindowsThemeFile.With(Bytes(Sample), new WindowsThemeSettings(WindowsThemeMode.Dark, 0x0A1B2C)));

        Assert.Contains("ColorizationColor=0X0A1B2C\r\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaving_the_accent_out_leaves_the_line_alone()
    {
        var result = Text(WindowsThemeFile.With(Bytes(Sample), new WindowsThemeSettings(WindowsThemeMode.Light, Accent: null)));

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
        var result = WindowsThemeFile.With(original, new WindowsThemeSettings(WindowsThemeMode.Light, 0x102030));

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
        var result = WindowsThemeFile.With(original, new WindowsThemeSettings(WindowsThemeMode.Light, Accent: null));

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

        var result = Text(WindowsThemeFile.With(Bytes(tricky), new WindowsThemeSettings(WindowsThemeMode.Light, Accent: null)));

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

    /// <summary>
    /// The shape Windows actually writes: an eight-digit colour and the auto-accent flag.
    /// </summary>
    /// <remarks>
    /// Taken from the owner's own <c>Custom.theme</c> on 2026-08-27, because the trimmed sample
    /// above has neither, and every defect the live experiment found lived in exactly those two
    /// lines. A test suite built only on the tidy sample passed while the feature did nothing.
    /// </remarks>
    private const string AsWindowsWritesIt =
        "[VisualStyles]\r\n" +
        "Path=%SystemRoot%\\resources\\themes\\Aero\\Aero.msstyles\r\n" +
        "AutoColorization=1\r\n" +
        "ColorizationColor=0XC4533222\r\n" +
        "SystemMode=Dark\r\n" +
        "AppMode=Dark\r\n";

    /// <summary>
    /// The alpha byte is the glass opacity and survives a colour change.
    /// </summary>
    /// <remarks>
    /// Writing six digits over an eight-digit value sets alpha to zero — a visibly different
    /// desktop, produced by a change that only claimed to touch the colour.
    /// </remarks>
    [Fact]
    public void A_colour_with_an_alpha_byte_keeps_it()
    {
        var result = Text(WindowsThemeFile.With(Bytes(AsWindowsWritesIt), new WindowsThemeSettings(WindowsThemeMode.Dark, 0x10FF10)));

        Assert.Contains("ColorizationColor=0XC410FF10", result, StringComparison.Ordinal);
    }

    /// <summary>A file with no alpha does not acquire one.</summary>
    [Fact]
    public void A_colour_without_an_alpha_byte_does_not_gain_one()
    {
        var result = Text(WindowsThemeFile.With(Bytes(Sample), new WindowsThemeSettings(WindowsThemeMode.Dark, 0x10FF10)));

        Assert.Contains("ColorizationColor=0X10FF10", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Setting a colour switches off the flag that would make Windows ignore it.
    /// </summary>
    /// <remarks>
    /// With <c>AutoColorization=1</c> the accent comes from the wallpaper and the written colour is
    /// read by nobody. The owner's machine had it set to 1, so without this the whole feature would
    /// have applied cleanly, verified as applied, and changed no colour at all.
    /// </remarks>
    [Fact]
    public void Setting_a_colour_stops_Windows_picking_its_own()
    {
        var result = Text(WindowsThemeFile.With(Bytes(AsWindowsWritesIt), new WindowsThemeSettings(WindowsThemeMode.Dark, 0x10FF10)));

        Assert.Contains("AutoColorization=0", result, StringComparison.Ordinal);
    }

    /// <summary>Changing only the mode leaves the accent arrangement alone.</summary>
    [Fact]
    public void Changing_only_the_mode_leaves_the_auto_accent_flag_alone()
    {
        var result = Text(WindowsThemeFile.With(Bytes(AsWindowsWritesIt), new WindowsThemeSettings(WindowsThemeMode.Light, Accent: null)));

        Assert.Contains("AutoColorization=1", result, StringComparison.Ordinal);
        Assert.Contains("ColorizationColor=0XC4533222", result, StringComparison.Ordinal);
        Assert.Contains("SystemMode=Light", result, StringComparison.Ordinal);
    }

    /// <summary>A key the file never had is not invented, here as everywhere else.</summary>
    [Fact]
    public void A_file_without_the_auto_accent_flag_does_not_gain_one()
    {
        var result = Text(WindowsThemeFile.With(Bytes(Sample), new WindowsThemeSettings(WindowsThemeMode.Dark, 0x10FF10)));

        Assert.DoesNotContain("AutoColorization", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// An eight-digit colour reads back as the colour, not as a negative number.
    /// </summary>
    /// <remarks>
    /// <c>0XC4533222</c> does not fit a signed int. Parsed as one it came back negative, and every
    /// later comparison against the colour it was read from disagreed with itself.
    /// </remarks>
    [Fact]
    public void An_eight_digit_colour_reads_back_as_its_three_colour_bytes()
    {
        var settings = WindowsThemeFile.Read(Bytes(AsWindowsWritesIt));

        Assert.Equal(0x533222, settings.Accent);
    }

    [Fact]
    public void A_file_that_leaves_the_accent_to_Windows_says_so()
    {
        Assert.True(WindowsThemeFile.Read(Bytes(AsWindowsWritesIt)).IsAccentAutomatic);
        Assert.False(WindowsThemeFile.Read(Bytes(Sample)).IsAccentAutomatic);
    }

    /// <summary>
    /// What Windows did with a real file, kept as a test.
    /// </summary>
    /// <remarks>
    /// Measured on 2026-08-27: a theme carrying <c>ColorizationColor=0XC410FF10</c> was applied and
    /// Windows wrote <c>DWM\AccentColor=0xFF10FF10</c>. That pins the byte order in both directions
    /// against an observation rather than against a reading of the two values side by side, which is
    /// how the earlier note in this area got it backwards.
    /// </remarks>
    [Fact]
    public void The_accent_conversion_matches_what_Windows_did_with_a_real_file()
    {
        Assert.Equal(0x10FF10, WindowsThemeFile.AccentFromExplorer(0xFF10FF10));
        Assert.Equal(0xFF10FF10, WindowsThemeFile.AccentToExplorer(0x10FF10));

        // The colour that was already on the machine, which is not a palindrome.
        Assert.Equal(0x533222, WindowsThemeFile.AccentFromExplorer(0xFF223253));
        Assert.Equal(0xFF223253, WindowsThemeFile.AccentToExplorer(0x533222));
    }

    /// <summary>
    /// Handing the accent back to Windows is a state a file can be asked for.
    /// </summary>
    /// <remarks>
    /// This is what an undo needs on a machine that was picking its accent from the wallpaper.
    /// Without it every undo would leave the setting switched off — changing something the person
    /// chose, by way of an action whose entire purpose is to change nothing.
    /// </remarks>
    [Fact]
    public void Asking_Windows_to_choose_the_accent_again_writes_the_flag_back()
    {
        var wanted = new WindowsThemeSettings(WindowsThemeMode.Dark, 0x10FF10, IsAccentAutomatic: true);
        var result = Text(WindowsThemeFile.With(Bytes(AsWindowsWritesIt), wanted));

        Assert.Contains("AutoColorization=1", result, StringComparison.Ordinal);

        // The colour is left where it was: Windows is about to choose one anyway, and overwriting
        // it would discard what the machine had without being asked to.
        Assert.Contains("ColorizationColor=0XC4533222", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wanted_state_with_no_mode_leaves_both_mode_lines_alone()
    {
        var result = Text(WindowsThemeFile.With(Bytes(AsWindowsWritesIt), new WindowsThemeSettings(null, 0x10FF10)));

        Assert.Contains("SystemMode=Dark", result, StringComparison.Ordinal);
        Assert.Contains("AppMode=Dark", result, StringComparison.Ordinal);
        Assert.Contains("ColorizationColor=0XC410FF10", result, StringComparison.Ordinal);
    }
}
