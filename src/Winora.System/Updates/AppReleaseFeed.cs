using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Winora.System.Updates;

/// <summary>Looks up the newest published Winora release.</summary>
public interface IAppReleaseFeed
{
    /// <summary>The newest release, or null when there is not one we can act on.</summary>
    Task<AppRelease?> LatestAsync(CancellationToken cancellationToken = default);
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
/// Returns null for everything that is not a complete, usable release. There is no error to report
/// and nothing for the person to do about it: a check that failed is indistinguishable, from where
/// they sit, from there being no new version, and inventing a difference would only add noise.
/// </para>
/// </remarks>
public sealed class AppReleaseFeed : IAppReleaseFeed
{
    private const string DefaultUrl =
        "https://api.github.com/repos/geniyhackerdotaswag-bit/Winora/releases/latest";

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

    public async Task<AppRelease?> LatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await _http
                .GetFromJsonAsync<GithubRelease>(_url, cancellationToken)
                .ConfigureAwait(false);

            if (release is null || AppVersion.Parse(release.TagName) is not { } version)
            {
                return null;
            }

            var program = Asset(release, ProgramAsset);
            var checksum = Asset(release, ChecksumAsset);

            // Both or neither. Without the checksum the download cannot be verified, and offering
            // an update that would then be refused wastes the person's time.
            if (program?.DownloadUrl is not { Length: > 0 } programUrl ||
                checksum?.DownloadUrl is not { Length: > 0 } checksumUrl)
            {
                return null;
            }

            return new AppRelease(
                version,
                release.TagName ?? string.Empty,
                release.Body ?? string.Empty,
                programUrl,
                checksumUrl,
                program.Size,
                release.PublishedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // No network, a rate limit, or a changed feed shape. Null means "not known", which
            // AppUpdateCheck deliberately does not turn into "an update is available".
            return null;
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
