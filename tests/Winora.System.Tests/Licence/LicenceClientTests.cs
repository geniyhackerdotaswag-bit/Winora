using System.Net;
using System.Text;
using Winora.Core.Licence;
using Winora.System.Licence;
using Xunit;

namespace Winora.System.Tests.Licence;

/// <summary>
/// Turning what the site said into something the screen can act on.
/// </summary>
/// <remarks>
/// The site can say four different noes, and each sends a person somewhere different: to their
/// typing, to the site to renew, to the cabinet to free a machine slot, or to their network. These
/// tests exist because collapsing them into one "не удалось" would send them nowhere — the exact
/// failure that made "не удалось создать резервную копию" unfixable for two days.
/// </remarks>
public sealed class LicenceClientTests
{
    private const string Site = "https://example.invalid";
    private const string GoodKey = "WNR-2345-6789-ABCD-EFGH";

    private static LicenceClient Answering(HttpStatusCode status, string json) =>
        new(new HttpClient(new CannedHandler(status, json)), Site);

    [Fact]
    public async Task A_mistyped_key_is_refused_before_anything_is_sent()
    {
        // The handler throws if reached: nothing may go to the network for a key that cannot be one.
        var client = new LicenceClient(new HttpClient(new ExplodingHandler()), Site);

        var (result, token) = await client.ActivateAsync("не ключ", "PC", null, default);

        Assert.Equal(LicenceOutcome.Malformed, result.Outcome);
        Assert.Empty(token);
    }

    [Fact]
    public async Task A_build_without_a_site_says_so_rather_than_blaming_the_network()
    {
        var client = new LicenceClient(new HttpClient(new ExplodingHandler()), string.Empty);

        var (result, _) = await client.ActivateAsync(GoodKey, "PC", null, default);

        Assert.Equal(LicenceOutcome.NotConfigured, result.Outcome);
        Assert.Equal(LicenceOutcome.NotConfigured, (await client.CheckAsync("token", default)).Outcome);
    }

    [Fact]
    public async Task An_accepted_key_comes_back_with_a_token_and_a_date()
    {
        var client = Answering(HttpStatusCode.OK, """
            {"token":"abc123","plan":"month","expires_at":"2026-10-03 12:00:00","bonus_days":14}
            """);

        var (result, token) = await client.ActivateAsync(GoodKey, "DESKTOP-1", "НОВЫЙГОД", default);

        Assert.Equal(LicenceOutcome.Activated, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Equal("abc123", token);
        Assert.Equal("month", result.State.Plan);
        Assert.Equal("DESKTOP-1", result.State.Machine);
        Assert.Equal(14, result.BonusDays);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
            result.State.ExpiresUtc);
    }

    /// <summary>Each refusal keeps its own name all the way to the screen.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"error":"bad_key"}""", LicenceOutcome.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":"expired"}""", LicenceOutcome.Expired)]
    [InlineData(HttpStatusCode.Conflict, """{"error":"device_limit","device_limit":3}""", LicenceOutcome.DeviceLimit)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"bad_key_format"}""", LicenceOutcome.Malformed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":"storage_unavailable"}""", LicenceOutcome.Unreachable)]
    public async Task Every_refusal_keeps_its_own_name(
        HttpStatusCode status,
        string json,
        LicenceOutcome expected)
    {
        var (result, token) = await Answering(status, json).ActivateAsync(GoodKey, "PC", null, default);

        Assert.Equal(expected, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Empty(token);
    }

    [Fact]
    public async Task A_full_key_reports_how_many_machines_it_allows()
    {
        var (result, _) = await Answering(
            HttpStatusCode.Conflict,
            """{"error":"device_limit","device_limit":3}""").ActivateAsync(GoodKey, "PC", null, default);

        Assert.Equal(LicenceOutcome.DeviceLimit, result.Outcome);
        Assert.Equal(3, result.DeviceLimit);
    }

    /// <summary>
    /// A page of HTML where JSON was expected is a network problem, not a crash.
    /// </summary>
    /// <remarks>
    /// A captive portal, a proxy or a misconfigured host answers with a login page and status 200.
    /// Letting the JSON parser throw here would take down the screen that exists to fix it.
    /// </remarks>
    [Fact]
    public async Task An_answer_that_is_not_json_is_reported_as_unreachable()
    {
        var (result, _) = await Answering(HttpStatusCode.OK, "<html>вход в сеть</html>")
            .ActivateAsync(GoodKey, "PC", null, default);

        Assert.Equal(LicenceOutcome.Unreachable, result.Outcome);
    }

    /// <summary>A success without an end date cannot be stored, so it is not treated as one.</summary>
    [Fact]
    public async Task A_success_without_a_date_is_not_a_success()
    {
        var (result, _) = await Answering(HttpStatusCode.OK, """{"token":"abc","plan":"month"}""")
            .ActivateAsync(GoodKey, "PC", null, default);

        Assert.Equal(LicenceOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task A_check_takes_the_server_clock_as_the_moment_it_happened()
    {
        var result = await Answering(HttpStatusCode.OK, """
            {"plan":"year","expires_at":"2027-01-01 00:00:00","server_time":"2026-09-03 10:00:00"}
            """).CheckAsync("token", default);

        Assert.Equal(LicenceOutcome.Confirmed, result.Outcome);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero),
            result.State.CheckedUtc);
    }

    /// <summary>
    /// The site's dates are read as UTC wherever the machine thinks it is.
    /// </summary>
    /// <remarks>
    /// Without saying so, the same string becomes a different instant in every time zone, and a
    /// subscription would end hours early for one person and hours late for another.
    /// </remarks>
    [Fact]
    public void A_date_from_the_site_is_read_as_universal_time()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 9, 3, 21, 40, 0, TimeSpan.Zero),
            LicenceClient.ParseUtc("2026-09-03 21:40:00"));

        Assert.Null(LicenceClient.ParseUtc(null));
        Assert.Null(LicenceClient.ParseUtc("не дата"));
    }

    [Fact]
    public async Task A_check_without_a_token_never_reaches_the_network()
    {
        var client = new LicenceClient(new HttpClient(new ExplodingHandler()), Site);

        Assert.Equal(LicenceOutcome.Rejected, (await client.CheckAsync(string.Empty, default)).Outcome);
    }

    private sealed class CannedHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>Fails the test if the code under test sends anything at all.</summary>
    private sealed class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Nothing should have been sent.");
    }
}
