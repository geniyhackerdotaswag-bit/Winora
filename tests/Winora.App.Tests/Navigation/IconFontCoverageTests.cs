using System.Buffers.Binary;
using Winora.App.Controls;
using Winora.System.Windows;
using Xunit;

namespace Winora.App.Tests.Navigation;

/// <summary>
/// Every icon in the catalog must exist in both icon fonts, not just the newer one.
/// </summary>
/// <remarks>
/// <para>
/// Winora falls back from "Segoe Fluent Icons" to "Segoe MDL2 Assets" on machines that lack the
/// first — Windows 10, and the stripped builds that people who tune Windows tend to run. The
/// fallback is only worth having if it can draw everything, and nothing about the catalog says
/// which code points the older font has. So it is read out of the font file.
/// </para>
/// <para>
/// This is what stops the next icon from silently breaking Windows 10: the newer font has code
/// points the older one never had, and picking one of those would pass every other test in this
/// repository while shipping an empty box to a whole class of machines.
/// </para>
/// <para>
/// Reads the <c>cmap</c> table directly rather than asking a font API. The parser is thirty lines,
/// runs in a plain xUnit host with no COM, and reads the same table the text engine reads.
/// Microsoft Learn: https://learn.microsoft.com/en-us/typography/opentype/spec/cmap
/// </para>
/// </remarks>
public sealed class IconFontCoverageTests
{
    private static string FontPath(string fileName) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", fileName);

    /// <summary>
    /// Checks the catalog against every icon font present on the machine running the tests.
    /// </summary>
    /// <remarks>
    /// One test over both fonts rather than one per font, because a font that is not installed here
    /// cannot be checked and must not fail the run — the whole reason the fallback exists is that
    /// not every machine has both. What must never happen is the check quietly examining nothing,
    /// so the count of fonts actually read is asserted at the end.
    /// </remarks>
    [Fact]
    public void Every_catalog_glyph_exists_in_every_icon_font_on_this_machine()
    {
        (string Family, string File)[] fonts =
        [
            (IconFontProbe.PreferredFamily, "SegoeIcons.ttf"),
            (IconFontProbe.FallbackFamily, "segmdl2.ttf"),
        ];

        var checkedFonts = 0;
        var missing = new List<string>();

        foreach (var (family, fileName) in fonts)
        {
            var path = FontPath(fileName);

            if (!File.Exists(path))
            {
                continue;
            }

            checkedFonts++;
            var covered = CodePointsIn(path);

            foreach (var key in FluentIconCatalog.Keys)
            {
                if (!FluentIconCatalog.TryGetGlyph(key, out var glyph))
                {
                    // A vector-path icon carries its own shape and needs no font.
                    continue;
                }

                var codePoint = char.ConvertToUtf32(glyph, 0);
                if (!covered.Contains(codePoint))
                {
                    missing.Add($"{family}: {key} (U+{codePoint:X4})");
                }
            }
        }

        Assert.True(
            checkedFonts > 0,
            "Neither icon font is installed, so nothing was checked. This test must not pass by " +
            "examining nothing.");

        Assert.True(
            missing.Count == 0,
            $"No glyph for: {string.Join("; ", missing)}. " +
            "Pick a code point both icon fonts carry, or Windows 10 draws an empty box.");
    }

    /// <summary>Every code point the font can draw, from its character map.</summary>
    private static HashSet<int> CodePointsIn(string path)
    {
        var file = File.ReadAllBytes(path);
        var cmap = TableOffset(file, "cmap")
            ?? throw new InvalidDataException($"'{path}' has no cmap table.");

        var subtable = BestSubtable(file, cmap);
        var format = ReadUInt16(file, subtable);

        return format switch
        {
            4 => FromFormat4(file, subtable),
            12 => FromFormat12(file, subtable),
            _ => throw new InvalidDataException($"Unsupported cmap format {format} in '{path}'."),
        };
    }

    private static int? TableOffset(byte[] file, string tag)
    {
        var tables = ReadUInt16(file, 4);

        for (var i = 0; i < tables; i++)
        {
            var record = 12 + (i * 16);
            // Rooted at global: inside this namespace "System.Text" resolves to Winora.System.Text.
            if (global::System.Text.Encoding.ASCII.GetString(file, record, 4) == tag)
            {
                return (int)ReadUInt32(file, record + 8);
            }
        }

        return null;
    }

    /// <summary>
    /// Format 12 when the font has one, format 4 otherwise.
    /// </summary>
    /// <remarks>
    /// Format 12 is preferred because it reaches past U+FFFF. Both icon fonts keep their glyphs in
    /// the private use area below that, so format 4 alone would do — but a font that gained a
    /// supplementary-plane glyph would then read as not covering it.
    /// </remarks>
    private static int BestSubtable(byte[] file, int cmap)
    {
        var count = ReadUInt16(file, cmap + 2);
        int? format4 = null;

        for (var i = 0; i < count; i++)
        {
            var record = cmap + 4 + (i * 8);
            var subtable = cmap + (int)ReadUInt32(file, record + 4);

            switch (ReadUInt16(file, subtable))
            {
                case 12:
                    return subtable;
                case 4:
                    format4 ??= subtable;
                    break;
            }
        }

        return format4 ?? throw new InvalidDataException("No usable cmap subtable.");
    }

    private static HashSet<int> FromFormat4(byte[] file, int subtable)
    {
        var doubled = ReadUInt16(file, subtable + 6);
        var segments = doubled / 2;
        var ends = subtable + 14;
        var starts = ends + doubled + 2;
        var covered = new HashSet<int>();

        for (var i = 0; i < segments; i++)
        {
            int first = ReadUInt16(file, starts + (i * 2));
            int last = ReadUInt16(file, ends + (i * 2));

            // The final segment is the required 0xFFFF terminator, not a real range.
            if (first == 0xFFFF && last == 0xFFFF)
            {
                continue;
            }

            for (var code = first; code <= last; code++)
            {
                covered.Add(code);
            }
        }

        return covered;
    }

    private static HashSet<int> FromFormat12(byte[] file, int subtable)
    {
        var groups = (int)ReadUInt32(file, subtable + 12);
        var covered = new HashSet<int>();

        for (var i = 0; i < groups; i++)
        {
            var group = subtable + 16 + (i * 12);
            var first = (int)ReadUInt32(file, group);
            var last = (int)ReadUInt32(file, group + 4);

            for (var code = first; code <= last; code++)
            {
                covered.Add(code);
            }
        }

        return covered;
    }

    private static ushort ReadUInt16(byte[] file, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(offset, 2));

    private static uint ReadUInt32(byte[] file, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset, 4));
}
