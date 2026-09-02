using Winora.System.Windows;

namespace Winora.App.Services;

/// <param name="Id">Which pack, and the folder name a downloaded copy sits in.</param>
/// <param name="Name">What the card calls it.</param>
/// <param name="Author">Who made it.</param>
/// <param name="PreviewUrl">A picture of the pointer, for a card with nothing on disk yet.</param>
/// <param name="SizeBytes">How large the download is.</param>
public sealed record CursorOfferView(
    string Id,
    string Name,
    string Author,
    string PreviewUrl,
    long SizeBytes);

/// <summary>How a download ended, for the presentation layer.</summary>
public enum CursorDownloadResult
{
    Installed,

    /// <summary>Nothing arrived. Nothing was changed.</summary>
    DownloadFailed,

    /// <summary>What arrived was not an archive this build can open.</summary>
    Unreadable,

    /// <summary>The archive held no cursor at all.</summary>
    Empty,

    /// <summary>It could not be written into the cursors folder.</summary>
    NotStored,
}

/// <summary>The packs Winora offers to fetch, for the presentation layer.</summary>
public interface ICursorDownloadService
{
    Task<IReadOnlyList<CursorOfferView>> OffersAsync(CancellationToken cancellationToken = default);

    Task<CursorDownloadResult> DownloadAsync(
        string id,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// Stands between <see cref="ViewModels.CursorsViewModel"/> and Winora.System the same way
/// <see cref="BypassService"/> does for the bypass: <c>Winora.Architecture.Tests.
/// SolutionStructureTests.ViewModels_never_reference_infrastructure_or_system_directly</c> forbids a
/// ViewModel from naming Winora.System at all, so the translation into presentation vocabulary has
/// to happen here.
/// </remarks>
public sealed class CursorDownloadService : ICursorDownloadService
{
    private readonly ICursorCatalogue _catalogue;

    /// <summary>
    /// The listings from the last look, kept so a download names a pack by its identifier.
    /// </summary>
    /// <remarks>
    /// The screen asks to download "chroma-black", not a record it was handed earlier. Resolving
    /// the identifier here means a card can never ask for a URL of its own — the only addresses
    /// ever fetched are the ones the catalogue published.
    /// </remarks>
    private IReadOnlyList<CursorPackListing> _seen = [];

    public CursorDownloadService(ICursorCatalogue catalogue)
    {
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
    }

    public async Task<IReadOnlyList<CursorOfferView>> OffersAsync(
        CancellationToken cancellationToken = default)
    {
        _seen = await _catalogue.ListAsync(cancellationToken).ConfigureAwait(true);

        return [.. _seen.Select(static listing => new CursorOfferView(
            listing.Id,
            listing.Name,
            listing.Author,
            listing.PreviewUrl,
            listing.SizeBytes))];
    }

    public async Task<CursorDownloadResult> DownloadAsync(
        string id,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        var listing = _seen.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.Ordinal));

        if (listing is null)
        {
            return CursorDownloadResult.DownloadFailed;
        }

        var outcome = await _catalogue
            .DownloadAsync(listing, progress, cancellationToken)
            .ConfigureAwait(true);

        return outcome switch
        {
            CursorDownloadOutcome.Installed => CursorDownloadResult.Installed,
            CursorDownloadOutcome.DownloadFailed => CursorDownloadResult.DownloadFailed,
            CursorDownloadOutcome.Unreadable => CursorDownloadResult.Unreadable,
            CursorDownloadOutcome.Empty => CursorDownloadResult.Empty,
            _ => CursorDownloadResult.NotStored,
        };
    }
}
