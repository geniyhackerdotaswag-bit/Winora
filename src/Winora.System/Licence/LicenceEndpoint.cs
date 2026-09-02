namespace Winora.System.Licence;

/// <summary>Where the Winora site lives.</summary>
/// <remarks>
/// <para>
/// One constant, in one file, so pointing a build at a different site is one edit and not a search.
/// It is deliberately allowed to be empty: a build that does not know the address answers
/// <c>NotConfigured</c> and says so on screen, rather than reporting a network failure for a site
/// that was never named.
/// </para>
/// <para>
/// Https only, and that is checked rather than assumed. A key travels in this request body; over
/// http it travels in the clear past every machine on the way, and the one credential Winora has
/// would be readable by all of them.
/// </para>
/// </remarks>
public static class LicenceEndpoint
{
    /// <summary>
    /// The site's address, without a trailing slash. Empty until the owner names their domain.
    /// </summary>
    public const string BaseUrl = "";

    /// <summary>Whether this build can reach a site at all.</summary>
    public static bool IsConfigured => IsUsable(BaseUrl);

    /// <summary>Whether an address is one this program will send a key to.</summary>
    public static bool IsUsable(string? address) =>
        !string.IsNullOrWhiteSpace(address) &&
        Uri.TryCreate(address, UriKind.Absolute, out var parsed) &&
        parsed.Scheme == Uri.UriSchemeHttps;
}
