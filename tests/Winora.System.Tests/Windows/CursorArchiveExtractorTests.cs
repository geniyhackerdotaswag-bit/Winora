using System.IO.Compression;
using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// Unpacking is the point where a file from a stranger meets an elevated process, so the guards are
/// tested directly rather than inferred from the code reading correctly.
/// </summary>
public sealed class CursorArchiveExtractorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("winora-cursor-zip").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    [Fact]
    public void Cursor_files_are_unpacked_next_to_the_archive()
    {
        var pack = Path.Combine(_root, "вариант2");
        Directory.CreateDirectory(pack);
        CreateArchive(Path.Combine(pack, "chroma.zip"), ("normal_select.ani", "cursor"));

        var count = new ArchiveExtractor(".cur", ".ani").ExtractPending(_root);

        Assert.Equal(1, count);
        Assert.True(File.Exists(Path.Combine(pack, "chroma", "normal_select.ani")));
    }

    /// <summary>
    /// The exclusion that keeps the whole feature safe. An installer script is the dangerous part of
    /// a downloaded pack and must never reach disk from here.
    /// </summary>
    [Fact]
    public void Only_cursor_entries_are_written()
    {
        var pack = Path.Combine(_root, "pack");
        Directory.CreateDirectory(pack);
        CreateArchive(
            Path.Combine(pack, "p.zip"),
            ("normal_select.cur", "cursor"),
            ("install.inf", "[Version]"),
            ("setup.exe", "MZ"),
            ("scheme.reg", "REGEDIT4"),
            ("readme.txt", "hi"));

        new ArchiveExtractor(".cur", ".ani").ExtractPending(_root);

        var written = Directory.GetFiles(Path.Combine(pack, "p"))
            .Select(static file => Path.GetFileName(file))
            .ToArray();

        Assert.Equal("normal_select.cur", Assert.Single(written));
    }

    /// <summary>
    /// Zip slip. An entry naming a path outside the destination must not be able to write there —
    /// this app runs elevated, so a traversal would land with administrative rights.
    /// </summary>
    [Fact]
    public void An_entry_that_tries_to_escape_the_folder_writes_nothing_outside_it()
    {
        var pack = Path.Combine(_root, "evil");
        Directory.CreateDirectory(pack);
        var escapeTarget = Path.Combine(_root, "pwned.cur");
        CreateArchive(
            Path.Combine(pack, "e.zip"),
            (@"..\..\pwned.cur", "cursor"),
            ("normal_select.cur", "cursor"));

        new ArchiveExtractor(".cur", ".ani").ExtractPending(_root);

        Assert.False(File.Exists(escapeTarget));
        Assert.True(File.Exists(Path.Combine(pack, "e", "normal_select.cur")));
        Assert.True(File.Exists(Path.Combine(pack, "e", "pwned.cur")));
    }

    [Fact]
    public void An_already_extracted_archive_is_left_alone()
    {
        var pack = Path.Combine(_root, "pack");
        Directory.CreateDirectory(pack);
        CreateArchive(Path.Combine(pack, "p.zip"), ("normal_select.cur", "first"));

        var extractor = new ArchiveExtractor(".cur", ".ani");
        extractor.ExtractPending(_root);

        var file = Path.Combine(pack, "p", "normal_select.cur");
        File.WriteAllText(file, "edited by the user");

        var second = extractor.ExtractPending(_root);

        Assert.Equal(0, second);
        Assert.Equal("edited by the user", File.ReadAllText(file));
    }

    [Fact]
    public void A_corrupt_archive_does_not_take_the_scan_down()
    {
        var pack = Path.Combine(_root, "broken");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "b.zip"), "this is not a zip");

        var count = new ArchiveExtractor(".cur", ".ani").ExtractPending(_root);

        Assert.Equal(0, count);
    }

    /// <summary>The scanner must find cursors that an archive placed one level down.</summary>
    [Fact]
    public void An_archived_pack_becomes_visible_to_the_scanner()
    {
        var pack = Path.Combine(_root, "вариант2");
        Directory.CreateDirectory(pack);
        CreateArchive(
            Path.Combine(pack, "chroma.zip"),
            ("normal_select.ani", "cursor"),
            ("busy.ani", "cursor"));

        var found = Assert.Single(new CursorFolderScanner(_root).Packs());

        // Named after the archive, not the folder: a user's own folder is often called "вариант2"
        // while the download inside says what the pack actually is.
        Assert.Equal("Chroma", found.Name);
        Assert.Equal(2, found.Files.Count);
    }

    private static void CreateArchive(string path, params (string Name, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }
}
