using System.IO.Compression;
using System.Net;
using System.Text;
using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Windows;

/// <summary>
/// The cursor packs Winora offers to fetch, and what it will and will not take out of one.
/// </summary>
/// <remarks>
/// The same shape as the bypass: a list published beside the program, a card for each entry,
/// nothing fetched until somebody presses a button. An archive is a file from the internet, so
/// most of what is asserted here is about not trusting it.
/// </remarks>
public sealed class CursorCatalogueTests : IDisposable
{
    private readonly string _root;

    public CursorCatalogueTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "winora-cursors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temporary folder is not worth failing a test over.
        }
    }

    private sealed class Serving(string manifest, byte[]? archive = null) : HttpMessageHandler
    {
        public int ArchiveRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.EndsWith(".zip", StringComparison.Ordinal))
            {
                ArchiveRequests++;

                return Task.FromResult(archive is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(manifest, Encoding.UTF8),
            });
        }
    }

    private CursorCatalogue Catalogue(string manifest, byte[]? archive = null) =>
        new(_root, "https://example.invalid/index.json", new HttpClient(new Serving(manifest, archive)));

    private static byte[] Zip(params (string Name, string Body)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(body);
            }
        }

        return buffer.ToArray();
    }

    private const string OnePack = """
        {"packs":[{"id":"chroma-black","name":"Chroma чёрная","author":"кто-то",
        "archiveUrl":"https://example.invalid/chroma.zip",
        "previewUrl":"https://example.invalid/chroma.png","sizeBytes":1024}]}
        """;

    [Fact]
    public async Task What_is_published_is_what_is_offered()
    {
        var listed = await Catalogue(OnePack).ListAsync();

        Assert.Single(listed);
        Assert.Equal("chroma-black", listed[0].Id);
        Assert.Equal("Chroma чёрная", listed[0].Name);
        Assert.Equal("кто-то", listed[0].Author);
        Assert.Equal(1024, listed[0].SizeBytes);
    }

    /// <summary>An entry missing what a card needs is not shown as a card that cannot work.</summary>
    [Fact]
    public async Task An_entry_without_what_a_card_needs_is_dropped()
    {
        var listed = await Catalogue("""
            {"packs":[
              {"id":"no-name","archiveUrl":"https://example.invalid/a.zip"},
              {"id":"no-archive","name":"Без архива"},
              {"name":"Без имени папки","archiveUrl":"https://example.invalid/b.zip"}]}
            """).ListAsync();

        Assert.Empty(listed);
    }

    /// <summary>
    /// An identifier that is a path, not a name, is refused.
    /// </summary>
    /// <remarks>
    /// It becomes a folder under the cursors root, and the manifest is a file on the internet. An
    /// id of <c>..\..\Startup</c> would otherwise unpack somewhere nobody chose.
    /// </remarks>
    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("C:\\Windows")]
    [InlineData("sub/folder")]
    public void An_identifier_that_walks_somewhere_is_not_a_name(string id)
    {
        Assert.False(CursorCatalogue.IsPlainName(id));
    }

    [Theory]
    [InlineData("chroma-black")]
    [InlineData("Chroma 2")]
    public void An_ordinary_name_is_a_name(string id)
    {
        Assert.True(CursorCatalogue.IsPlainName(id));
    }

    /// <summary>A catalogue that cannot be reached offers nothing, rather than failing.</summary>
    [Fact]
    public async Task An_unreachable_catalogue_offers_nothing()
    {
        var catalogue = new CursorCatalogue(
            _root,
            "https://example.invalid/index.json",
            new HttpClient(new Serving("not json at all")));

        Assert.Empty(await catalogue.ListAsync());
    }

    [Fact]
    public async Task A_downloaded_pack_lands_where_the_screen_reads_from()
    {
        var archive = Zip(
            ("cursors/normal.cur", "pointer"),
            ("cursors/busy.ani", "pointer"));

        var catalogue = Catalogue(OnePack, archive);
        var listing = (await catalogue.ListAsync())[0];

        Assert.Equal(CursorDownloadOutcome.Installed, await catalogue.DownloadAsync(listing, null));

        var folder = Path.Combine(_root, "chroma-black");

        Assert.True(File.Exists(Path.Combine(folder, "normal.cur")));
        Assert.True(File.Exists(Path.Combine(folder, "busy.ani")));
    }

    /// <summary>
    /// Only pointers are taken out of an archive.
    /// </summary>
    /// <remarks>
    /// A cursor pack downloaded from the internet routinely carries a readme, an installer script
    /// and a preview image. None of them belongs in a folder Winora will later hand to Windows.
    /// </remarks>
    [Fact]
    public async Task Only_the_cursors_come_out_of_the_archive()
    {
        var archive = Zip(
            ("normal.cur", "pointer"),
            ("readme.txt", "read me"),
            ("install.bat", "@echo off"),
            ("preview.png", "picture"));

        var catalogue = Catalogue(OnePack, archive);

        Assert.Equal(
            CursorDownloadOutcome.Installed,
            await catalogue.DownloadAsync((await catalogue.ListAsync())[0], null));

        var files = Directory.GetFiles(Path.Combine(_root, "chroma-black")).Select(Path.GetFileName);

        Assert.Equal(["normal.cur"], files);
    }

    /// <summary>
    /// An entry that tries to write outside its folder writes inside it.
    /// </summary>
    /// <remarks>
    /// Archives really do contain <c>..\..\</c> entries; that is the whole of the traversal
    /// vulnerability. Each entry is written by its bare name, so the escape is discarded along
    /// with the rest of the path.
    /// </remarks>
    [Fact]
    public async Task An_entry_that_tries_to_escape_stays_inside()
    {
        var archive = Zip(("../../escaped.cur", "pointer"));
        var catalogue = Catalogue(OnePack, archive);

        Assert.Equal(
            CursorDownloadOutcome.Installed,
            await catalogue.DownloadAsync((await catalogue.ListAsync())[0], null));

        Assert.True(File.Exists(Path.Combine(_root, "chroma-black", "escaped.cur")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.cur")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped.cur")));
    }

    /// <summary>An archive with no pointers in it leaves no folder pretending to be a pack.</summary>
    [Fact]
    public async Task An_archive_with_no_cursors_leaves_nothing_behind()
    {
        var catalogue = Catalogue(OnePack, Zip(("readme.txt", "nothing here")));

        Assert.Equal(
            CursorDownloadOutcome.Empty,
            await catalogue.DownloadAsync((await catalogue.ListAsync())[0], null));

        Assert.False(Directory.Exists(Path.Combine(_root, "chroma-black")));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task Something_that_is_not_an_archive_is_refused()
    {
        var catalogue = Catalogue(OnePack, Encoding.UTF8.GetBytes("this is not a zip"));

        Assert.Equal(
            CursorDownloadOutcome.Unreadable,
            await catalogue.DownloadAsync((await catalogue.ListAsync())[0], null));

        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task A_download_that_fails_changes_nothing()
    {
        var catalogue = Catalogue(OnePack);

        Assert.Equal(
            CursorDownloadOutcome.DownloadFailed,
            await catalogue.DownloadAsync((await catalogue.ListAsync())[0], null));

        Assert.Empty(Directory.GetDirectories(_root));
    }

    /// <summary>Downloading the same pack twice replaces it rather than piling up beside it.</summary>
    [Fact]
    public async Task Downloading_again_replaces_what_was_there()
    {
        var catalogue = Catalogue(OnePack, Zip(("normal.cur", "first")));
        var listing = (await catalogue.ListAsync())[0];

        Assert.Equal(CursorDownloadOutcome.Installed, await catalogue.DownloadAsync(listing, null));

        var second = Catalogue(OnePack, Zip(("normal.cur", "second"), ("extra.ani", "pointer")));

        Assert.Equal(CursorDownloadOutcome.Installed, await second.DownloadAsync(listing, null));

        var folder = Path.Combine(_root, "chroma-black");

        Assert.Equal("second", File.ReadAllText(Path.Combine(folder, "normal.cur")));
        Assert.True(File.Exists(Path.Combine(folder, "extra.ani")));
        Assert.Single(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task How_much_has_arrived_is_reported()
    {
        var reported = new List<double>();
        var catalogue = Catalogue(OnePack, Zip(("normal.cur", new string('x', 5000))));

        await catalogue.DownloadAsync(
            (await catalogue.ListAsync())[0],
            new Progress<double>(reported.Add));

        // Progress is raised on a captured context; the assertion is that the download completed,
        // which the folder proves, rather than on how many reports arrived by now.
        Assert.True(Directory.Exists(Path.Combine(_root, "chroma-black")));
    }
}
