using Winora.System.Windows;

namespace Winora.App.Services;

/// <param name="Id">Stable location identifier.</param>
/// <param name="Path">Absolute path shown to the user.</param>
/// <param name="IsUserOwned">Whether Winora may ever reclaim from here.</param>
/// <param name="ReasonCode">Why a protected location is off-limits; null when it is not.</param>
/// <param name="FileCount">Files counted, or null when the location was not surveyed.</param>
/// <param name="TotalBytes">Bytes counted, or null when the location was not surveyed.</param>
/// <param name="IsFullyEnumerated">False when part of the location could not be read.</param>
public sealed record CleanupLocationView(
    string Id,
    string Path,
    bool IsUserOwned,
    string? ReasonCode,
    int? FileCount,
    long? TotalBytes,
    bool IsFullyEnumerated);

/// <param name="DeletedCount">Files removed.</param>
/// <param name="DeletedBytes">Space freed.</param>
/// <param name="SkippedCount">Files a running process held open.</param>
public sealed record CleanupOutcome(int DeletedCount, long DeletedBytes, int SkippedCount);

/// <summary>
/// Surveys and cleans reclamation candidates for the presentation layer, without letting a ViewModel
/// reference <c>Winora.System</c> directly.
/// </summary>
public interface ICleanupSurveyService
{
    IReadOnlyList<CleanupLocationView> Survey(CancellationToken cancellationToken);

    /// <summary>
    /// Permanently deletes the contents of one reclaimable location and records the decision in the
    /// action journal. Deleting and journalling are one call because a caller must not be able to do
    /// the first without the second.
    /// </summary>
    Task<CleanupOutcome> CleanAsync(string locationId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class CleanupSurveyService : ICleanupSurveyService
{
    private readonly ITempLocationProbe _probe;
    private readonly ITempCleaner _cleaner;
    private readonly IActionJournalWriter _journal;

    public CleanupSurveyService(
        ITempLocationProbe probe,
        ITempCleaner cleaner,
        IActionJournalWriter journal)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _cleaner = cleaner ?? throw new ArgumentNullException(nameof(cleaner));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public IReadOnlyList<CleanupLocationView> Survey(CancellationToken cancellationToken)
    {
        var results = new List<CleanupLocationView>();

        foreach (var location in _probe.Locations())
        {
            if (!TempReclamationPolicy.CanReclaim(location, _probe.IsElevated))
            {
                // Still listed, so the screen can say why rather than leaving the user to wonder.
                results.Add(new CleanupLocationView(
                    location.Id,
                    location.Path,
                    IsUserOwned: false,
                    location.ReasonCode,
                    FileCount: null,
                    TotalBytes: null,
                    IsFullyEnumerated: true));
                continue;
            }

            var survey = _probe.Survey(location, cancellationToken);
            results.Add(new CleanupLocationView(
                location.Id,
                location.Path,
                IsUserOwned: true,
                // The reason code is kept even when the location is now reclaimable. It carries the
                // consequence of clearing — losing update rollback, for one — and that is truest at
                // the moment the user can actually act on it. Only the "needs administrator" part
                // belongs to the refusal, and the screen adds that separately.
                location.ReasonCode,
                survey.FileCount,
                survey.TotalBytes,
                survey.IsFullyEnumerated));
        }

        return results;
    }

    public async Task<CleanupOutcome> CleanAsync(string locationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);

        // Resolved from the probe rather than taken from the caller, so a location the probe does not
        // classify as user-owned cannot be reached even if the UI asked for it.
        var location = _probe.Locations().FirstOrDefault(candidate =>
            string.Equals(candidate.Id, locationId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"'{locationId}' is not a known cleanup location.");

        var requiredElevation =
            location.Classification == TempLocationClassification.RequiresElevation;

        TempCleanResult result;
        try
        {
            result = await Task.Run(
                () => _cleaner.Clean(location, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Journalled before it is rethrown. A reclamation that failed part-way is precisely the
            // entry someone comes looking for, and the count is left null rather than guessed at
            // zero, because files may well have gone before the failure.
            await _journal.RecordReclamationAsync(
                location.Id,
                location.Path,
                requiredElevation,
                succeeded: false,
                deletedCount: null,
                cancellationToken).ConfigureAwait(false);
            throw;
        }

        await _journal.RecordReclamationAsync(
            location.Id,
            location.Path,
            requiredElevation,
            succeeded: true,
            result.DeletedCount,
            cancellationToken).ConfigureAwait(false);

        return new CleanupOutcome(result.DeletedCount, result.DeletedBytes, result.SkippedCount);
    }
}
