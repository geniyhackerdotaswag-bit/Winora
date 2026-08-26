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

    public async Task<AppReleaseLookup> LatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await _http
                .GetFromJsonAsync<GithubRelease>(_url, cancellationToken)
                .ConfigureAwait(false);

            if (release is null || AppVersion.Parse(release.TagName) is not { } version)
            {
                return AppReleaseLookup.Answered(null);
            }

            var program = Asset(release, ProgramAsset);
            var checksum = Asset(release, ChecksumAsset);

            // Both or neither. Without the checksum the download cannot be verified, and offering
            // an update that would then be refused wastes the person's time.
            if (program?.DownloadUrl is not { Length: > 0 } programUrl ||
                checksum?.DownloadUrl is not { Length: > 0 } checksumUrl)
            {
                return AppReleaseLookup.Answered(null);
            }

            return AppReleaseLookup.Answered(new AppRelease(
                version,
                release.TagName ?? string.Empty,
                release.Body ?? string.Empty,
                programUrl,
                checksumUrl,
                program.Size,
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
