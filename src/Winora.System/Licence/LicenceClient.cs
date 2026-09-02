using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Core.Licence;

namespace Winora.System.Licence;

/// <summary>What the site was asked, and what it said.</summary>
public interface ILicenceClient
{
    /// <summary>Trades a key for a token this machine keeps.</summary>
    ValueTask<(LicenceResult Result, string Token)> ActivateAsync(
        string key,
        string machine,
        string? promoCode,
        CancellationToken cancellationToken);

    /// <summary>Asks the site whether a stored token still buys anything.</summary>
    ValueTask<LicenceResult> CheckAsync(string token, CancellationToken cancellationToken);
}

/// <summary>
/// Talks to the Winora site about subscriptions.
/// </summary>
/// <remarks>
/// <para>
/// Every failure here comes back as a named <see cref="LicenceOutcome"/>, never as an exception and
/// never as a bare false. The screen has to tell somebody whether to check their typing, free a
/// machine slot, renew, or wait for the network — four different actions, and one "не удалось"
/// sends them to none of them.
/// </para>
/// <para>
/// The address lives in <see cref="LicenceEndpoint"/> and can be empty. A build that does not know
/// where the site is answers <see cref="LicenceOutcome.NotConfigured"/> and never pretends the
/// network was at fault.
/// </para>
/// </remarks>
public sealed class LicenceClient : ILicenceClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public LicenceClient()
        : this(CreateClient(), LicenceEndpoint.BaseUrl)
    {
    }

    public LicenceClient(HttpClient http, string baseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
    }

    /// <remarks>
    /// Twenty seconds. Activation happens while somebody watches a spinner after typing a key they
    /// paid for; a minute of nothing is how a working site gets reported as broken.
    /// </remarks>
    private static HttpClient CreateClient() => new() { Timeout = TimeSpan.FromSeconds(20) };

    public async ValueTask<(LicenceResult Result, string Token)> ActivateAsync(
        string key,
        string machine,
        string? promoCode,
        CancellationToken cancellationToken)
    {
        // Refused here, before the request. A mistyped key is the common case, and the site cannot
        // answer it any better than this can.
        if (!LicenceKey.IsWellFormed(key))
        {
            return (LicenceResult.Failed(LicenceOutcome.Malformed), string.Empty);
        }

        if (_baseUrl.Length == 0)
        {
            return (LicenceResult.Failed(LicenceOutcome.NotConfigured), string.Empty);
        }

        HttpResponseMessage response;

        try
        {
            response = await _http.PostAsJsonAsync(
                _baseUrl + "/api/activate.php",
                new ActivateRequest(LicenceKey.Format(key), machine, promoCode),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return (LicenceResult.Failed(LicenceOutcome.Unreachable), string.Empty);
        }

        var body = await ReadAsync<ActivateResponse>(response, cancellationToken).ConfigureAwait(false);

        if (body is null)
        {
            return (FailureFor(response.StatusCode, null), string.Empty);
        }

        if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(body.Token))
        {
            return (FailureFor(response.StatusCode, body.Error, body.DeviceLimit), string.Empty);
        }

        var expires = ParseUtc(body.ExpiresAt);

        if (expires is null)
        {
            // A success without an end date is not a success we can store: everything downstream
            // reads that date, and a null there would read as "no key entered".
            return (LicenceResult.Failed(LicenceOutcome.Unreachable), string.Empty);
        }

        return (
            new LicenceResult(
                LicenceOutcome.Activated,
                new LicenceState(body.Plan ?? string.Empty, expires, machine, DateTimeOffset.UtcNow),
                body.BonusDays),
            body.Token);
    }

    public async ValueTask<LicenceResult> CheckAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token))
        {
            return LicenceResult.Failed(LicenceOutcome.Rejected);
        }

        if (_baseUrl.Length == 0)
        {
            return LicenceResult.Failed(LicenceOutcome.NotConfigured);
        }

        HttpResponseMessage response;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/api/licence.php");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return LicenceResult.Failed(LicenceOutcome.Unreachable);
        }

        var body = await ReadAsync<LicenceResponse>(response, cancellationToken).ConfigureAwait(false);

        if (body is null || !response.IsSuccessStatusCode)
        {
            return FailureFor(response.StatusCode, body?.Error);
        }

        var expires = ParseUtc(body.ExpiresAt);

        if (expires is null)
        {
            return LicenceResult.Failed(LicenceOutcome.Unreachable);
        }

        // The server's own clock, not this machine's. Storing it as the moment of the check is what
        // makes moving the local clock back pointless: the stored check does not get younger.
        var checkedAt = ParseUtc(body.ServerTime) ?? DateTimeOffset.UtcNow;

        return new LicenceResult(
            LicenceOutcome.Confirmed,
            new LicenceState(body.Plan ?? string.Empty, expires, null, checkedAt));
    }

    /// <summary>
    /// Turns a status code and the site's own error word into one named outcome.
    /// </summary>
    /// <remarks>
    /// The word is preferred over the code where both exist, because the site says more with it:
    /// 403 covers both a key that does not exist and one whose time has run out, and those two send
    /// a person to different places.
    /// </remarks>
    private static LicenceResult FailureFor(HttpStatusCode status, string? error, int deviceLimit = 0) =>
        error switch
        {
            "bad_key" or "bad_token" => LicenceResult.Failed(LicenceOutcome.Rejected),
            "bad_key_format" => LicenceResult.Failed(LicenceOutcome.Malformed),
            "expired" => LicenceResult.Failed(LicenceOutcome.Expired),
            "device_limit" => LicenceResult.Failed(LicenceOutcome.DeviceLimit, deviceLimit),
            _ => status == HttpStatusCode.Conflict
                ? LicenceResult.Failed(LicenceOutcome.DeviceLimit, deviceLimit)
                : LicenceResult.Failed(LicenceOutcome.Unreachable),
        };

    /// <summary>
    /// Reads the body as JSON, or null when it is not JSON at all.
    /// </summary>
    /// <remarks>
    /// Null rather than a throw: a proxy, a captive portal or a misconfigured host answers with
    /// HTML, and that is a network problem to report, not a crash to propagate.
    /// </remarks>
    private static async ValueTask<TBody?> ReadAsync<TBody>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        where TBody : class
    {
        try
        {
            return await response.Content
                .ReadFromJsonAsync<TBody>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the site's time format: <c>2026-09-03 21:40:00</c>, always UTC.
    /// </summary>
    /// <remarks>
    /// Parsed as UTC explicitly rather than left to the machine's locale. Without
    /// <c>AssumeUniversal</c> the same string becomes a different instant in every time zone, and a
    /// subscription would end three hours early for somebody and three hours late for somebody else.
    /// </remarks>
    public static DateTimeOffset? ParseUtc(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private sealed record ActivateRequest(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("machine")] string Machine,
        [property: JsonPropertyName("promo")] string? Promo);

    private sealed record ActivateResponse(
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("plan")] string? Plan,
        [property: JsonPropertyName("expires_at")] string? ExpiresAt,
        [property: JsonPropertyName("bonus_days")] int BonusDays,
        [property: JsonPropertyName("device_limit")] int DeviceLimit,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record LicenceResponse(
        [property: JsonPropertyName("plan")] string? Plan,
        [property: JsonPropertyName("expires_at")] string? ExpiresAt,
        [property: JsonPropertyName("server_time")] string? ServerTime,
        [property: JsonPropertyName("error")] string? Error);
}
