using Winora.System.Updates;

namespace Winora.App.Services;

/// <param name="Version">The release version, already formatted for display.</param>
/// <param name="Tag">The tag as written, for linking to the release page.</param>
/// <param name="Notes">What the release says about itself. May be empty.</param>
/// <param name="SizeBytes">How large the download is, so the strip can say before it starts.</param>
/// <remarks>
/// <see cref="Notes"/> and <see cref="SizeBytes"/> default so the many tests that do not care about
/// them can keep constructing this with just a version and a tag.
/// </remarks>
public sealed record AppUpdateReleaseView(string Version, string Tag, string Notes = "", long SizeBytes = 0);

/// <summary>How an update attempt ended, for the presentation layer.</summary>
public enum AppUpdateOutcomeView
{
    /// <summary>The new program is in place and the process may now restart into it.</summary>
    Installed,

    /// <summary>Nothing arrived. The program is unchanged.</summary>
    DownloadFailed,

    /// <summary>What arrived was not what was promised. The program is unchanged.</summary>
    Verification,

    /// <summary>The file could not be put in place. The program is unchanged.</summary>
    SwapFailed,

    /// <summary>This copy is not the installed one, so it is not ours to replace.</summary>
    NotInstalled,

    /// <summary>
    /// The program was moved aside and could not be put back. It is beside its own path under the
    /// <c>.old</c> suffix.
    /// </summary>
    Displaced,

    /// <summary>
    /// <see cref="IAppUpdateService.UpdateAsync"/> was called without a preceding
    /// <see cref="IAppUpdateService.CheckAsync"/> that found something to install.
    /// </summary>
    /// <remarks>
    /// Deliberately not folded into <see cref="NotInstalled"/>: that value means this copy is not
    /// sitting at the installed path, which is a fact about where the program is. This one means
    /// nothing has been offered yet, which is a fact about what has been agreed to -- the copy could
    /// well be the installed one. Collapsing the two used to route this case through the same
    /// "swap failed" message as a real swap failure, which is not what happened here.
    /// </remarks>
    NoUpdateOffered,
}

/// <summary>Winora's own release feed and self-update, for the presentation layer.</summary>
public interface IAppUpdateService
{
    /// <summary>True when this copy is running from the place an installed one belongs.</summary>
    bool IsInstalled { get; }

    /// <summary>Clears away what a previous update left behind. Called once at startup.</summary>
    void RemoveLeftovers();

    /// <summary>Checks for a release newer than <paramref name="currentVersion"/>.</summary>
    Task<AppUpdateReleaseView?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default);

    /// <summary>Installs only what a preceding <see cref="CheckAsync"/> offered.</summary>
    Task<AppUpdateOutcomeView> UpdateAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>Starts the installed copy. The caller ends this process afterwards.</summary>
    bool Restart();
}

/// <inheritdoc />
/// <remarks>
/// Stands between <see cref="Winora.App.ViewModels.UpdateViewModel" /> and Winora.System.Updates the
/// same way <see cref="BypassService" /> stands between the bypass ViewModel and its own System-layer
/// types: <c>Winora.Architecture.Tests.SolutionStructureTests.
/// ViewModels_never_reference_infrastructure_or_system_directly</c> forbids a ViewModel from naming
/// Winora.System at all, so the translation into presentation-layer vocabulary has to happen here.
/// </remarks>
public sealed class AppUpdateService : IAppUpdateService
{
    private readonly IAppReleaseFeed _feed;
    private readonly IAppUpdater _updater;
    private readonly IAppInstallLocation _location;

    /// <summary>
    /// The release found by the last check, kept so installing does not have to ask again — and so
    /// what gets installed is exactly what the user was shown and agreed to.
    /// </summary>
    private AppRelease? _offered;

    public AppUpdateService(IAppReleaseFeed feed, IAppUpdater updater, IAppInstallLocation location)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _updater = updater ?? throw new ArgumentNullException(nameof(updater));
        _location = location ?? throw new ArgumentNullException(nameof(location));
    }

    public bool IsInstalled => _location.IsInstalled;

    public void RemoveLeftovers() => _updater.RemoveLeftovers();

    public async Task<AppUpdateReleaseView?> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (AppVersion.Parse(currentVersion) is not { } current)
        {
            _offered = null;
            return null;
        }

        AppRelease? latest;
        try
        {
            latest = await _feed.LatestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Not knowing and there being nothing new look the same from where the person sits, and
            // a check that failed is not something they can act on. The feed's own timeout arrives
            // here as a cancellation, so this cannot be narrowed to "network errors" by type.
            latest = null;
        }

        var check = new AppUpdateCheck(current, latest);
        _offered = check.UpdateAvailable ? check.Latest : null;

        return _offered is null
            ? null
            : new AppUpdateReleaseView(
                _offered.Version.ToString(3),
                _offered.Tag,
                _offered.Notes,
                _offered.SizeBytes);
    }

    /// <remarks>
    /// Installs only what a preceding check offered. Without one there is nothing the user has been
    /// shown and agreed to, and this downloads and swaps in Winora's own executable — not something
    /// to do on an unseen version.
    /// </remarks>
    public async Task<AppUpdateOutcomeView> UpdateAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        if (_offered is not { } release)
        {
            return AppUpdateOutcomeView.NoUpdateOffered;
        }

        var outcome = await _updater.UpdateAsync(release, progress, cancellationToken).ConfigureAwait(false);

        return outcome switch
        {
            UpdateOutcome.Installed => AppUpdateOutcomeView.Installed,
            UpdateOutcome.DownloadFailed => AppUpdateOutcomeView.DownloadFailed,
            UpdateOutcome.Verification => AppUpdateOutcomeView.Verification,
            UpdateOutcome.Displaced => AppUpdateOutcomeView.Displaced,
            UpdateOutcome.SwapFailed => AppUpdateOutcomeView.SwapFailed,
            UpdateOutcome.NotInstalled => AppUpdateOutcomeView.NotInstalled,
            _ => AppUpdateOutcomeView.SwapFailed,
        };
    }

    public bool Restart() => _updater.Restart();
}
