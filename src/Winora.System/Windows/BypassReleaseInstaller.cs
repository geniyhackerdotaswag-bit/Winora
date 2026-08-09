using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Winora.System.Windows;

/// <param name="Tag">The release tag, which is also the installed-version marker.</param>
/// <param name="PublishedAtUtc">When it was published.</param>
/// <param name="DownloadUrl">The archive to fetch.</param>
/// <param name="SizeBytes">How large it is, so the screen can say before downloading.</param>
public sealed record BypassRelease(string Tag, DateTimeOffset PublishedAtUtc, string DownloadUrl, long SizeBytes);

/// <param name="InstalledTag">The tag currently unpacked, or empty when nothing is.</param>
/// <param name="Latest">The newest published release, or null when it could not be read.</param>
public sealed record BypassReleaseCheck(string InstalledTag, BypassRelease? Latest)
{
    /// <summary>
    /// True only when both tags are known and differ. An unreadable feed is never an update.
    /// </summary>
    public bool UpdateAvailable =>
        Latest is not null &&
        Latest.Tag.Length > 0 &&
        !string.Equals(InstalledTag, Latest.Tag, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Fetches and unpacks the bypass release.</summary>
public interface IBypassReleaseInstaller
{
    /// <summary>The tag currently unpacked, or empty.</summary>
    string InstalledTag();

    /// <summary>Looks up the newest release without downloading it.</summary>
    Task<BypassReleaseCheck> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads and unpacks a release, replacing whatever is there.</summary>
    Task<bool> InstallAsync(
        BypassRelease release,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Downloads <c>zapret-discord-youtube</c> from its GitHub releases and unpacks it.
/// </summary>
/// <remarks>
/// <para>
/// Winora fetches the upstream project's own published archive and does not modify it. The tool is
/// Flowseal's; this only puts it on disk and reads the strategies out of it.
/// </para>
/// <para>
/// Nothing here installs anything on its own. The screen shows the tag and the publication date and
/// waits for the user to agree, because this downloads an executable that will later run with
/// administrator rights — the one place in Winora where a silent background update would be
/// indefensible.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.zipfile
/// </remarks>
public sealed class BypassReleaseInstaller : IBypassReleaseInstaller
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest";

    /// <summary>Records which release is unpacked. Written last, after a successful unpack.</summary>
    private const string TagFileName = "winora-installed-tag.txt";

    private readonly string _root;
    private readonly HttpClient _http;

    public BypassReleaseInstaller()
        : this(new BypassStrategyCatalog().RootDirectory, CreateClient())
    {
    }

    public BypassReleaseInstaller(string rootDirectory, HttpClient http)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <remarks>GitHub refuses requests without a user agent, with a 403 that looks like a ban.</remarks>
    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Winora");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public string InstalledTag()
    {
        try
        {
            var file = Path.Combine(_root, TagFileName);
            return File.Exists(file) ? File.ReadAllText(file).Trim() : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public async Task<BypassReleaseCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        var installed = InstalledTag();

        try
        {
            var release = await _http
                .GetFromJsonAsync<GithubRelease>(LatestReleaseUrl, cancellationToken)
                .ConfigureAwait(false);

            // The release carries several files; the zip is the one holding the tool.
            var asset = release?.Assets?.FirstOrDefault(static a =>
                a.Name is { Length: > 0 } && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            if (release?.TagName is not { Length: > 0 } tag || asset?.DownloadUrl is not { Length: > 0 } url)
            {
                return new BypassReleaseCheck(installed, null);
            }

            return new BypassReleaseCheck(
                installed,
                new BypassRelease(tag, release.PublishedAt, url, asset.Size));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // No network, a rate limit, or a changed feed shape. A null latest means "not known",
            // which the check above deliberately does not turn into "an update is available".
            return new BypassReleaseCheck(installed, null);
        }
    }

    public async Task<bool> InstallAsync(
        BypassRelease release,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        var archive = Path.Combine(Path.GetTempPath(), $"winora-zapret-{Guid.NewGuid():N}.zip");

        try
        {
            await DownloadAsync(release, archive, progress, cancellationToken).ConfigureAwait(false);

            // Unpacked into a staging folder first. Extracting over a live install would leave a
            // half-replaced release behind if anything failed partway.
            var staging = Path.Combine(Path.GetTempPath(), $"winora-zapret-{Guid.NewGuid():N}");
            ZipFile.ExtractToDirectory(archive, staging);

            var payload = LocateExecutableRoot(staging);
            if (payload is null)
            {
                return false;
            }

            Replace(payload, _root);

            // Written last: the tag is what says "this is installed", so it must not exist unless
            // the files it describes are actually in place.
            await File.WriteAllTextAsync(
                Path.Combine(_root, TagFileName),
                release.Tag,
                cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            TryDelete(archive);
        }
    }

    private async Task DownloadAsync(
        BypassRelease release,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? release.SizeBytes;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long written = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;

            if (total > 0)
            {
                progress?.Report(Math.Clamp((double)written / total, 0, 1));
            }
        }
    }

    /// <summary>
    /// Finds the folder that actually holds the tool.
    /// </summary>
    /// <remarks>
    /// The archive may unpack straight into files, or into a single versioned folder containing
    /// them. Rather than assume either shape — which would break the next time upstream changes how
    /// they package it — the executable is searched for and its grandparent is the release root.
    /// </remarks>
    private static string? LocateExecutableRoot(string staging)
    {
        try
        {
            var executable = Directory
                .EnumerateFiles(staging, "winws.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            return Path.GetDirectoryName(Path.GetDirectoryName(executable));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Replace(string source, string destination)
    {
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(source, destination);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // A leftover temp file is not worth reporting a failed install over.
        }
    }

    private sealed record GithubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset PublishedAt { get; init; }

        [JsonPropertyName("assets")]
        public IReadOnlyList<GithubAsset>? Assets { get; init; }
    }

    private sealed record GithubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? DownloadUrl { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }
    }
}
