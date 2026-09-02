using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Winora.System.Updates;

/// <summary>What a look at the release feed produced.</summary>
/// <param name="Reached">
/// False when the feed could not be asked at all — no network, a rate limit, a repository that is
/// not public, a changed answer shape.
/// </param>
/// <param name="Release">
/// The newest usable release, or null. Null with <paramref name="Reached"/> true means the feed
/// answered and had nothing to offer, which is a different fact.
/// </param>
public readonly record struct AppReleaseLookup(bool Reached, AppRelease? Release)
{
    /// <summary>The feed could not be asked.</summary>
    public static AppReleaseLookup Unreachable => new(false, null);

    /// <summary>The feed answered. The release may still be null.</summary>
    public static AppReleaseLookup Answered(AppRelease? release) => new(true, release);
}

/// <summary>Looks up the newest published Winora release.</summary>
public interface IAppReleaseFeed
{
    /// <summary>The newest release, and whether the feed could be reached at all.</summary>
    Task<AppReleaseLookup> LatestAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the newest release out of the project's own GitHub releases.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately shaped like <see cref="Windows.BypassReleaseInstaller" />, which holds the same
/// conversation with the same service: the user agent GitHub insists on, and the rule that an
/// unreadable answer is never an update. What differs is the comparison, which is by version rather
/// than by text — see <see cref="AppUpdateCheck.UpdateAvailable" />.
/// </para>
/// <para>
/// Returns null for everything that is not a complete, usable release, and says separately whether
/// the feed could be asked at all. Those were folded together until 2026-08-26, on the argument
/// that a failed check and no new version look the same from where the person sits and that
/// inventing a difference would only add noise. That holds for one dropped request. It does not
/// hold for a failure that persists — a repository that is not public answers 404 every time, and
/// the program then reports "you have the latest version" for ever, having never once managed to
/// ask. Who is told about it is decided upstream: nobody, unless they pressed the button.
/// </para>
/// </remarks>
public sealed class AppReleaseFeed : IAppReleaseFeed
{
    /// <summary>
    /// The releases, newest first — not the single one GitHub calls "latest".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A repository holds more than one kind of release. On 2026-09-02 the cursor packs were
    /// published under the tag <c>cursors-v1</c>, and <c>/releases/latest</c> immediately began
    /// answering with that: it means "the most recently published release", not "the most recent
    /// version of the program". Winora could not read a version out of the tag, concluded there was
    /// nothing newer, and told everybody they were up to date while an update sat one entry below.
    /// </para>
    /// <para>
    /// Marking that release as a pre-release hides it from <c>/latest</c> again, and it was, but
    /// that is a promise somebody has to keep by hand every time. Reading the list and taking the
    /// newest entry that actually looks like a version of this program is a promise the code keeps.
    /// </para>
    /// </remarks>
    private const string DefaultUrl =
        "https://api.github.com/repos/geniyhackerdotaswag-bit/Winora/releases?per_page=20";

    /// <summary>The program itself, as named in the release.</summary>
    private const string ProgramAsset = "Winora.exe";

    /// <summary>Its checksum, published by the same workflow run.</summary>
    private const string ChecksumAsset = "Winora.exe.sha256";

    private readonly HttpClient _http;
    private readonly string _url;

    public AppReleaseFeed()
        : this(CreateClient(), DefaultUrl)
    {
    }

    public AppReleaseFeed(HttpClient http, string releasesUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(releasesUrl);
        _url = releasesUrl;
    }

    /// <remarks>GitHub refuses requests without a user agent, with a 403 that looks like a ban.</remarks>
    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Winora");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public async Task<AppReleaseLookup> LatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var releases = await _http
                .GetFromJsonAsync<IReadOnlyList<GithubRelease>>(_url, cancellationToken)
                .ConfigureAwait(false);

            if (releases is null)
            {
                return AppReleaseLookup.Answered(null);
            }

            // Highest version wins, not newest publication. A fix released for an older line would
            // otherwise offer itself as an upgrade to somebody already ahead of it.
            var newest = releases
                .Select(release => (Release: release, Version: AppVersion.Parse(release.TagName)))
                .Where(candidate => candidate.Version is not null)
                .OrderByDescending(candidate => candidate.Version!)
                .FirstOrDefault(candidate =>
                    Asset(candidate.Release, ProgramAsset)?.DownloadUrl is { Length: > 0 } &&
                    Asset(candidate.Release, ChecksumAsset)?.DownloadUrl is { Length: > 0 });

            // Both assets or neither. Without the checksum the download cannot be verified, and
            // offering an update that would then be refused wastes the person's time — so a release
            // missing either is passed over above rather than answered with here.
            if (newest.Release is not { } release || newest.Version is not { } version)
            {
                return AppReleaseLookup.Answered(null);
            }

            return AppReleaseLookup.Answered(new AppRelease(
                version,
                release.TagName ?? string.Empty,
                release.Body ?? string.Empty,
                Asset(release, ProgramAsset)!.DownloadUrl!,
                Asset(release, ChecksumAsset)!.DownloadUrl!,
                Asset(release, ProgramAsset)!.Size,
                release.PublishedAt));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // No network, a rate limit, a repository that is not public, or a changed answer shape.
            // Not knowing is never turned into an update being available; it is now also not turned
            // into there being none.
            return AppReleaseLookup.Unreachable;
        }
    }

    private static GithubAsset? Asset(GithubRelease release, string name) =>
        release.Assets?.FirstOrDefault(asset =>
            string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase));

    private sealed record GithubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt,
        [property: JsonPropertyName("assets")] IReadOnlyList<GithubAsset>? Assets);

    private sealed record GithubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("browser_download_url")] string? DownloadUrl);
}
