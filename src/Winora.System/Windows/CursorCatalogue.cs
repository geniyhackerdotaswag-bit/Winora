using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winora.System.Windows;

/// <param name="Id">Folder name the pack is unpacked into. Also what tells two packs apart.</param>
/// <param name="Name">What the card calls it.</param>
/// <param name="Author">Who made it. Shown on the card, because somebody did.</param>
/// <param name="ArchiveUrl">Where the archive lives.</param>
/// <param name="PreviewUrl">A picture of the pointer, for the card before anything is downloaded.</param>
/// <param name="SizeBytes">How large the download is, so the card can say before it starts.</param>
public sealed record CursorPackListing(
    string Id,
    string Name,
    string Author,
    string ArchiveUrl,
    string PreviewUrl,
    long SizeBytes);

/// <summary>How a download ended.</summary>
public enum CursorDownloadOutcome
{
    Installed,

    /// <summary>Nothing arrived. Nothing was changed.</summary>
    DownloadFailed,

    /// <summary>What arrived was not an archive this build can open.</summary>
    Unreadable,

    /// <summary>The archive held no cursor at all, so there is nothing to apply.</summary>
    Empty,

    /// <summary>It could not be written into the cursors folder.</summary>
    NotStored,
}

/// <summary>The packs Winora offers to fetch, and fetching them.</summary>
public interface ICursorCatalogue
{
    /// <summary>Where downloaded packs are put. The same folder the screen already reads.</summary>
    string RootDirectory { get; }

    /// <summary>What is on offer, or an empty list when the catalogue cannot be reached.</summary>
    Task<IReadOnlyList<CursorPackListing>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches one pack and unpacks it beside the ones already there.</summary>
    Task<CursorDownloadOutcome> DownloadAsync(
        CursorPackListing listing,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// The same shape as the bypass: a list published beside the program, a card for each entry, and
/// nothing fetched until somebody presses a button. What arrives is unpacked into the folder the
/// cursors screen already reads, so a downloaded pack and one the owner dropped in by hand are the
/// same thing from there on — there is no second kind of pack to keep track of.
/// </para>
/// <para>
/// Only <c>.cur</c> and <c>.ani</c> are taken out of an archive, and each is written by its bare
/// file name into the pack's own folder. An archive is a file from the internet: an entry named
/// <c>..\..\Windows\System32\x.dll</c> is a real thing that real archives contain, and joining it
/// to a folder resolves somewhere else entirely.
/// </para>
/// </remarks>
public sealed class CursorCatalogue : ICursorCatalogue
{
    /// <summary>Where the list of packs is published.</summary>
    public const string DefaultManifestUrl =
        "https://raw.githubusercontent.com/geniyhackerdotaswag-bit/Winora/main/assets/cursors/index.json";

    private static readonly string[] CursorExtensions = [".cur", ".ani"];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _root;
    private readonly string _manifestUrl;
    private readonly HttpClient _http;

    public CursorCatalogue()
        : this(new CursorFolderScanner().RootDirectory, DefaultManifestUrl, CreateClient())
    {
    }

    public CursorCatalogue(string rootDirectory, string manifestUrl, HttpClient http)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestUrl);

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        _manifestUrl = manifestUrl;
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public string RootDirectory => _root;

    /// <remarks>GitHub refuses requests without a user agent, with a 403 that reads like a ban.</remarks>
    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Winora");
        return http;
    }

    public async Task<IReadOnlyList<CursorPackListing>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var text = await _http.GetStringAsync(_manifestUrl, cancellationToken).ConfigureAwait(false);
            var listed = JsonSerializer.Deserialize<CursorManifest>(text, Options);

            return listed?.Packs is null
                ? []
                : [.. listed.Packs.Where(IsUsable).Select(static p => new CursorPackListing(
                    p.Id!, p.Name!, p.Author ?? string.Empty, p.ArchiveUrl!, p.PreviewUrl ?? string.Empty, p.SizeBytes))];
        }
        catch (Exception)
        {
            // No network, no catalogue, a 404 on the manifest. The screen still shows the packs
            // already on disk; an empty list is "nothing to offer", not a failure worth a dialog.
            return [];
        }
    }

    /// <summary>
    /// Whether a listing has the three things without which a card is a dead end.
    /// </summary>
    /// <remarks>
    /// The identifier is also checked for being a plain name. It becomes a folder underneath the
    /// cursors root, and a manifest is a file on the internet: an id of <c>..\..\Startup</c> would
    /// otherwise unpack somewhere nobody chose.
    /// </remarks>
    private static bool IsUsable(CursorManifestEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.Id) &&
        !string.IsNullOrWhiteSpace(entry.Name) &&
        !string.IsNullOrWhiteSpace(entry.ArchiveUrl) &&
        IsPlainName(entry.Id!);

    /// <summary>A name that is only a name: no separators, no drive, no walking upwards.</summary>
    public static bool IsPlainName(string candidate) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        candidate.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\', ':']) < 0 &&
        candidate is not "." and not "..";

    public async Task<CursorDownloadOutcome> DownloadAsync(
        CursorPackListing listing,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listing);

        if (!IsPlainName(listing.Id))
        {
            return CursorDownloadOutcome.Unreadable;
        }

        byte[] archive;

        try
        {
            archive = await DownloadArchiveAsync(listing, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CursorDownloadOutcome.DownloadFailed;
        }

        // Unpacked beside the folder rather than into it, then moved into place. A download that
        // fails halfway must not leave a folder that looks like a pack and is missing its pointers.
        var staging = Path.Combine(_root, "." + listing.Id + ".incoming");

        try
        {
            Directory.CreateDirectory(_root);
            DeleteQuietly(staging);
            Directory.CreateDirectory(staging);

            var taken = Extract(archive, staging);

            if (taken == 0)
            {
                DeleteQuietly(staging);
                return CursorDownloadOutcome.Empty;
            }

            var destination = Path.Combine(_root, listing.Id);
            DeleteQuietly(destination);
            Directory.Move(staging, destination);

            return CursorDownloadOutcome.Installed;
        }
        catch (InvalidDataException)
        {
            DeleteQuietly(staging);
            return CursorDownloadOutcome.Unreadable;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeleteQuietly(staging);
            return CursorDownloadOutcome.NotStored;
        }
    }

    private async Task<byte[]> DownloadArchiveAsync(
        CursorPackListing listing,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(listing.ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? listing.SizeBytes;
        using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        int read;

        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);

            if (total > 0)
            {
                progress?.Report(Math.Min(100d, buffer.Length * 100d / total));
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Takes the cursors out of an archive, and nothing else.
    /// </summary>
    /// <remarks>
    /// Each entry is written by its bare file name, so the folder structure inside the archive is
    /// discarded along with any attempt to escape the destination. Two entries with the same name
    /// in different folders collide, and the later one wins — which is the right outcome for a set
    /// of pointers and a poor reason to keep the paths that make traversal possible.
    /// </remarks>
    private static int Extract(byte[] archive, string destination)
    {
        using var stream = new MemoryStream(archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var taken = 0;

        foreach (var entry in zip.Entries)
        {
            var name = Path.GetFileName(entry.FullName);

            if (name.Length == 0 ||
                !CursorExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            entry.ExtractToFile(Path.Combine(destination, name), overwrite: true);
            taken++;
        }

        return taken;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // A folder that will not go is reported by the step that needed it gone.
        }
    }

    private sealed record CursorManifestEntry(
        string? Id,
        string? Name,
        string? Author,
        string? ArchiveUrl,
        string? PreviewUrl,
        long SizeBytes);

    private sealed record CursorManifest(IReadOnlyList<CursorManifestEntry>? Packs);
}
