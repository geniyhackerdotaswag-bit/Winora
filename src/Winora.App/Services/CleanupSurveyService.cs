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

/// <summary>
/// Surveys reclamation candidates for the presentation layer without letting a ViewModel reference
/// <c>Winora.System</c> directly. Read-only: nothing here moves or deletes anything.
/// </summary>
public interface ICleanupSurveyService
{
    IReadOnlyList<CleanupLocationView> Survey(CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class CleanupSurveyService : ICleanupSurveyService
{
    private readonly ITempLocationProbe _probe;

    public CleanupSurveyService(ITempLocationProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public IReadOnlyList<CleanupLocationView> Survey(CancellationToken cancellationToken)
    {
        var results = new List<CleanupLocationView>();

        foreach (var location in _probe.Locations())
        {
            if (location.Classification != TempLocationClassification.UserOwned)
            {
                // Protected and unavailable locations are still listed, so the screen can say why
                // Winora will not touch them rather than leaving the user to wonder.
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
}
