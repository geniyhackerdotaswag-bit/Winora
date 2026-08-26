using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Winora.System.Windows;

/// <param name="Tag">The release tag, which is also the installed-version marker.</param>
/// <param name="PublishedAtUtc">When it was published.</param>
/// <param name="DownloadUrl">The archive to fetch.</param>
/// <param name="SizeBytes">How large it is, so the screen can say before downloading.</param>
public sealed record BypassRelease(string Tag, DateTimeOffset PublishedAtUtc, string DownloadUrl, long SizeBytes);

/// <summary>How an install ended.</summary>
/// <remarks>
/// Every one of these used to be reported as "the antivirus probably deleted the files during
/// unpacking". That is one plausible cause stated as the only cause, and on 2026-08-27 it was told
/// to somebody whose real-time protection was switched off — while the disk had no free space at
/// all. A guess in the shape of a fact is the one thing this program is not supposed to do.
/// </remarks>
public enum BypassInstallOutcome
{
    Installed,

    /// <summary>The archive could not be fetched.</summary>
    DownloadFailed,

    /// <summary>There was not enough room to download or unpack it.</summary>
    NoDiskSpace,

    /// <summary>The archive arrived but could not be unpacked.</summary>
    ArchiveUnreadable,

    /// <summary>It unpacked, and what came out is not a usable release.</summary>
    PayloadIncomplete,

    /// <summary>The files are ready but the folder could not be replaced.</summary>
    FolderLocked,

    /// <summary>A driver from the installed release is loaded, and the kernel holds its file.</summary>
    DriverLoaded,
}

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
    /// <summary>The driver file that blocked the last install, or empty.</summary>
    string LockedDriverName { get; }

    Task<BypassInstallOutcome> InstallAsync(
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

    public async Task<BypassInstallOutcome> InstallAsync(
        BypassRelease release,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        var archive = Path.Combine(Path.GetTempPath(), $"winora-zapret-{Guid.NewGuid():N}.zip");
        var staging = Path.Combine(Path.GetTempPath(), $"winora-zapret-{Guid.NewGuid():N}");

        try
        {
            try
            {
                await DownloadAsync(release, archive, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return IsOutOfSpace(ex) ? BypassInstallOutcome.NoDiskSpace : BypassInstallOutcome.DownloadFailed;
            }

            // Unpacked into a staging folder first. Extracting over a live install would leave a
            // half-replaced release behind if anything failed partway.
            try
            {
                ZipFile.ExtractToDirectory(archive, staging);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return IsOutOfSpace(ex) ? BypassInstallOutcome.NoDiskSpace : BypassInstallOutcome.ArchiveUnreadable;
            }

            var payload = LocateExecutableRoot(staging);

            // Checked before anything is replaced, because what follows destroys the copy that
            // works. An unpack that lost half its files — a full disk, an antivirus, a truncated
            // download — must not be allowed to take the working install with it.
            if (payload is null || !IsCompleteRelease(payload))
            {
                return BypassInstallOutcome.PayloadIncomplete;
            }

            // Asked before the folder is touched. A loaded driver cannot be moved aside, and the
            // general advice for a locked folder — stop the bypass and try again — is wrong for it:
            // stopping winws.exe does not unload WinDivert, so somebody following that advice
            // repeats the same failure indefinitely.
            if (LoadedDriver(_root) is { } driver)
            {
                _lastLockedDriver = driver;
                return BypassInstallOutcome.DriverLoaded;
            }

            try
            {
                Replace(payload, _root);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return IsOutOfSpace(ex) ? BypassInstallOutcome.NoDiskSpace : BypassInstallOutcome.FolderLocked;
            }

            // Written last: the tag is what says "this is installed", so it must not exist unless
            // the files it describes are actually in place.
            await File.WriteAllTextAsync(
                Path.Combine(_root, TagFileName),
                release.Tag,
                cancellationToken).ConfigureAwait(false);

            return BypassInstallOutcome.Installed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return IsOutOfSpace(ex) ? BypassInstallOutcome.NoDiskSpace : BypassInstallOutcome.FolderLocked;
        }
        finally
        {
            TryDelete(archive);
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>The driver file that stopped the last install, for the message to name.</summary>
    private string _lastLockedDriver = string.Empty;

    /// <inheritdoc />
    public string LockedDriverName => _lastLockedDriver;

    /// <summary>
    /// The name of a driver file under <paramref name="folder"/> the kernel is holding, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked by trying to open each one for exclusive write, rather than by consulting the service
    /// list. A driver is only a problem here when its file cannot be moved, and that is exactly
    /// what this asks — no guessing from a service name that may or may not refer to this copy.
    /// </para>
    /// <para>
    /// Restricted to .sys files. Any other locked file is an ordinary lock, and the general advice
    /// for those — stop what is running and try again — is the right one.
    /// </para>
    /// </remarks>
    public static string? LoadedDriver(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return null;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.sys", SearchOption.AllDirectories))
            {
                if (IsLocked(file))
                {
                    return Path.GetFileName(file);
                }
            }
        }
        catch (Exception)
        {
            // Unreadable is not the same as locked, and claiming a driver holds the folder when
            // this could not look would be the guess this whole class of message exists to stop.
        }

        return null;
    }

    private static bool IsLocked(string file)
    {
        try
        {
            using var handle = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Whether what came out of the archive is a release this can run.
    /// </summary>
    /// <remarks>
    /// The executable alone is not enough: the tool is started by name from the strategy files and
    /// needs its driver beside it. Checked here so a half-unpacked archive is refused while the
    /// working copy is still on disk.
    /// </remarks>
    private static bool IsCompleteRelease(string payload)
    {
        try
        {
            return File.Exists(Path.Combine(payload, "bin", "winws.exe")) &&
                Directory.EnumerateFiles(payload, "*.bat", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether Windows was saying the disk is full.
    /// </summary>
    /// <remarks>
    /// ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL. Worth telling apart from everything else because
    /// it is the one cause the person can act on immediately, and because this program has a screen
    /// that clears space.
    /// </remarks>
    private static bool IsOutOfSpace(Exception ex) =>
        ex is IOException && (ex.HResult & 0xFFFF) is 0x27 or 0x70;

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

    /// <summary>
    /// Puts the new release where the old one was, keeping the old one until the new one is there.
    /// </summary>
    /// <remarks>
    /// This used to delete the destination and then move — and a failure between those two lines
    /// left the person with no bypass at all and a folder holding one stray file. Which is exactly
    /// what the owner's machine looked like on 2026-08-27. The working copy now steps aside instead
    /// of being destroyed, and comes back if the move does not finish.
    /// </remarks>
    private static void Replace(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var aside = destination + ".previous";
        TryDeleteDirectory(aside);

        var moved = false;

        if (Directory.Exists(destination))
        {
            Directory.Move(destination, aside);
            moved = true;
        }

        try
        {
            Directory.Move(source, destination);
        }
        catch (Exception)
        {
            if (moved)
            {
                TryDeleteDirectory(destination);
                Directory.Move(aside, destination);
            }

            throw;
        }

        TryDeleteDirectory(aside);
    }

    private static void TryDeleteDirectory(string path)
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
            // A leftover folder is not worth failing an install that otherwise worked.
        }
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
