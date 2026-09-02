using System.Net;
using System.Text;
using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Asking GitHub what the newest release is.
/// </summary>
/// <remarks>
/// Nothing here touches the network. The answers are the shapes GitHub actually returns, including
/// the ones that are not answers at all — a rate limit, a release published without its files, a
/// body that is not the JSON we expect. The rule those cases all check is the same one: not knowing
/// is not the same as knowing there is nothing, and neither is an update.
/// </remarks>
public sealed class AppReleaseFeedTests
{
    private const string Url = "https://example.invalid/releases/latest";

    /// <summary>An answer of the shape GitHub gives: a list, with both files attached.</summary>
    private static string Body(string tag) => "[" + One(tag) + "]";

    private static string One(string tag) => $$"""
        {
          "tag_name": "{{tag}}",
          "body": "Что нового: полоска обновления.",
          "published_at": "2026-08-23T12:00:00Z",
          "assets": [
            {
              "name": "Winora.exe",
              "size": 92274688,
              "browser_download_url": "https://example.invalid/Winora.exe"
            },
            {
              "name": "Winora.exe.sha256",
              "size": 64,
              "browser_download_url": "https://example.invalid/Winora.exe.sha256"
            }
          ]
        }
        """;

    private static AppReleaseFeed Feed(HttpStatusCode status, string body) =>
        new(new HttpClient(new CannedHandler(status, body)), Url);

    [Fact]
    public async Task The_newest_release_is_read()
    {
        var lookup = await Feed(HttpStatusCode.OK, Body("v0.4.0")).LatestAsync();

        Assert.True(lookup.Reached);
        var release = lookup.Release;
        Assert.NotNull(release);
        Assert.Equal(new Version(0, 4, 0), release.Version);
        Assert.Equal("v0.4.0", release.Tag);
        Assert.Equal("https://example.invalid/Winora.exe", release.DownloadUrl);
        Assert.Equal("https://example.invalid/Winora.exe.sha256", release.ChecksumUrl);
        Assert.Equal(92274688, release.SizeBytes);
        Assert.Contains("полоска", release.Notes);
    }

    /// <summary>
    /// Half a release is worse than none: without the checksum the download cannot be verified, and
    /// offering an update we would then refuse to install wastes the person's time and trust.
    /// </summary>
    [Fact]
    public async Task A_release_missing_its_checksum_is_not_a_release()
    {
        const string body = """
            [
            {
              "tag_name": "v0.4.0",
              "body": "",
              "published_at": "2026-08-23T12:00:00Z",
              "assets": [
                { "name": "Winora.exe", "size": 10, "browser_download_url": "https://example.invalid/Winora.exe" }
              ]
            }
            ]
            """;

        var lookup = await Feed(HttpStatusCode.OK, body).LatestAsync();

        Assert.True(lookup.Reached);
        Assert.Null(lookup.Release);
    }

    [Fact]
    public async Task A_release_missing_the_program_is_not_a_release()
    {
        const string body = """
            [
            {
              "tag_name": "v0.4.0",
              "body": "",
              "published_at": "2026-08-23T12:00:00Z",
              "assets": [
                { "name": "Winora.exe.sha256", "size": 64, "browser_download_url": "https://example.invalid/s" }
              ]
            }
            ]
            """;

        var lookup = await Feed(HttpStatusCode.OK, body).LatestAsync();

        Assert.True(lookup.Reached);
        Assert.Null(lookup.Release);
    }

    /// <summary>
    /// The feed could not be asked, which is not the same as it having nothing to offer.
    /// </summary>
    /// <remarks>
    /// A repository that is not public answers 404 to every check, for ever. Folded in with "no new
    /// version" — as these were until 2026-08-26 — the program tells everybody who presses the
    /// button that they have the latest version, having never once managed to ask.
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}")]
    [InlineData(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}")]
    [InlineData(HttpStatusCode.OK, "not json at all")]
    public async Task An_answer_we_cannot_read_means_we_did_not_ask(HttpStatusCode status, string body)
    {
        var lookup = await Feed(status, body).LatestAsync();

        Assert.False(lookup.Reached);
        Assert.Null(lookup.Release);
    }

    /// <summary>An answer we can read, holding nothing we can use.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("[{}]")]
    public async Task An_empty_answer_is_an_answer(string body)
    {
        var lookup = await Feed(HttpStatusCode.OK, body).LatestAsync();

        Assert.True(lookup.Reached);
        Assert.Null(lookup.Release);
    }

    /// <summary>
    /// A release that is not the program does not stop the program being updated.
    /// </summary>
    /// <remarks>
    /// Broken on 2026-09-02 and fixed the same hour. The cursor packs were published under the tag
    /// <c>cursors-v1</c>, and the feed was reading GitHub's <c>/releases/latest</c> — which means
    /// "the most recently published release", not "the newest version of the program". Winora could
    /// not read a version out of that tag, concluded there was nothing newer, and told the owner he
    /// had the latest version while v0.9.0 sat one entry below it.
    /// </remarks>
    [Fact]
    public async Task A_release_that_is_not_the_program_is_passed_over()
    {
        var body = "[" + $$"""
            {
              "tag_name": "cursors-v1",
              "body": "Наборы курсоров",
              "published_at": "2026-09-02T12:00:00Z",
              "assets": [
                { "name": "chroma-black-s.zip", "size": 137828, "browser_download_url": "https://example.invalid/c.zip" }
              ]
            }
            """ + "," + One("v0.9.0") + "]";

        var lookup = await Feed(HttpStatusCode.OK, body).LatestAsync();

        Assert.True(lookup.Reached);
        Assert.Equal("v0.9.0", lookup.Release?.Tag);
    }

    /// <summary>
    /// The highest version wins, not the one published most recently.
    /// </summary>
    /// <remarks>
    /// A fix released for an older line would otherwise offer itself as an upgrade to somebody
    /// already ahead of it, and they would install it and be moved backwards.
    /// </remarks>
    [Fact]
    public async Task The_highest_version_wins_not_the_newest_publication()
    {
        var body = "[" + One("v0.7.1") + "," + One("v0.9.0") + "]";

        var lookup = await Feed(HttpStatusCode.OK, body).LatestAsync();

        Assert.Equal("v0.9.0", lookup.Release?.Tag);
    }

    /// <summary>A tag nobody can parse is not a version, and so not an update.</summary>
    [Fact]
    public async Task A_tag_that_is_not_a_version_is_nothing()
    {
        var lookup = await Feed(HttpStatusCode.OK, Body("latest")).LatestAsync();

        Assert.True(lookup.Reached);
        Assert.Null(lookup.Release);
    }

    /// <summary>
    /// The comparison the whole feature turns on. Equal is not an update; older is not an update;
    /// and not knowing is not an update.
    /// </summary>
    [Theory]
    [InlineData("0.4.0", "0.3.0", false)]
    [InlineData("0.4.0", "0.4.0", false)]
    [InlineData("0.3.0", "0.4.0", true)]
    [InlineData("0.4.1", "0.4.0", false)]
    [InlineData("0.4.0", "0.4.1", true)]
    public void An_update_is_offered_only_when_the_release_is_newer(
        string current, string latest, bool expected)
    {
        var release = new AppRelease(
            AppVersion.Parse(latest)!, "v" + latest, string.Empty,
            "https://example.invalid/a", "https://example.invalid/b", 1,
            DateTimeOffset.UnixEpoch);

        var check = new AppUpdateCheck(AppVersion.Parse(current)!, release);

        Assert.Equal(expected, check.UpdateAvailable);
    }

    [Fact]
    public void Not_knowing_is_not_an_update()
    {
        Assert.False(new AppUpdateCheck(new Version(0, 1, 0), null).UpdateAvailable);
    }

    /// <summary>Answers a canned reply without going anywhere.</summary>
    private sealed class CannedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
