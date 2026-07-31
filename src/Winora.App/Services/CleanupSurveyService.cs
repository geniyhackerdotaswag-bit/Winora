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

    /// <summary>Permanently deletes the contents of one user-owned location.</summary>
    CleanupOutcome Clean(string locationId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class CleanupSurveyService : ICleanupSurveyService
{
    private readonly ITempLocationProbe _probe;
    private readonly ITempCleaner _cleaner;

    public CleanupSurveyService(ITempLocationProbe probe, ITempCleaner cleaner)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _cleaner = cleaner ?? throw new ArgumentNullException(nameof(cleaner));
    }

    public IReadOnlyList<CleanupLocationView> Survey(CancellationToken cancellationToken)
    {
        var results = new List<CleanupLocationView>();

        foreach (var location in _probe.Locations())
        {
            if (location.Classification != TempLocationClassification.UserOwned)
            {
                // Protected locations are still listed, so the screen can say why Winora will not
                // touch them rather than leaving the user to wonder.
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
                ReasonCode: null,
                survey.FileCount,
                survey.TotalBytes,
                survey.IsFullyEnumerated));
        }

        return results;
    }

    public CleanupOutcome Clean(string locationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);

        // Resolved from the probe rather than taken from the caller, so a location the probe does not
        // classify as user-owned cannot be reached even if the UI asked for it.
        var location = _probe.Locations().FirstOrDefault(candidate =>
            string.Equals(candidate.Id, locationId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"'{locationId}' is not a known cleanup location.");

        var result = _cleaner.Clean(location, cancellationToken);
        return new CleanupOutcome(result.DeletedCount, result.DeletedBytes, result.SkippedCount);
    }
}
