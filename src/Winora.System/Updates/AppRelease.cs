namespace Winora.System.Updates;

/// <param name="Version">The release version, already parsed and normalised to three numbers.</param>
/// <param name="Tag">The tag as written, for showing and for linking to the page.</param>
/// <param name="Notes">What the release says about itself. May be empty.</param>
/// <param name="DownloadUrl">Where <c>Winora.exe</c> is.</param>
/// <param name="ChecksumUrl">Where <c>Winora.exe.sha256</c> is.</param>
/// <param name="SizeBytes">How large the program is, so the screen can say before downloading.</param>
/// <param name="PublishedAtUtc">When it was published.</param>
public sealed record AppRelease(
    Version Version,
    string Tag,
    string Notes,
    string DownloadUrl,
    string ChecksumUrl,
    long SizeBytes,
    DateTimeOffset PublishedAtUtc);

/// <param name="Current">The version running now.</param>
/// <param name="Latest">The newest published release, or null when it could not be read.</param>
public sealed record AppUpdateCheck(Version Current, AppRelease? Latest)
{
    /// <summary>
    /// True only when a release was read and it is genuinely newer.
    /// </summary>
    /// <remarks>
    /// Compared as versions, not as text. The bypass feed compares its tags as strings, and for
    /// somebody else's tags that is right — their format is not ours to assume. These tags are ours,
    /// and string comparison would call every locally built version an update, because a working
    /// copy is almost always ahead of what has been published.
    /// </remarks>
    public bool UpdateAvailable => Latest is not null && Latest.Version > Current;
}
