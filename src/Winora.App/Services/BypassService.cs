using Winora.System.Windows;

namespace Winora.App.Services;

/// <param name="Id">Identifies the strategy back to the service.</param>
/// <param name="Name">The release's own name for it, unchanged.</param>
public sealed record BypassStrategyView(string Id, string Name);

/// <param name="StateResourceKey">What is running, as a resource key.</param>
/// <param name="IsOurs">True when Winora started what is running.</param>
/// <param name="IsAnythingRunning">True when any bypass is running, ours or not.</param>
/// <param name="ForeignPath">Which other copy is running, or empty.</param>
public sealed record BypassStatusView(
    string StateResourceKey,
    bool IsOurs,
    bool IsAnythingRunning,
    string ForeignPath);

/// <param name="Tag">The newest published release, or empty when it could not be read.</param>
/// <param name="PublishedAtUtc">When it was published.</param>
/// <param name="SizeBytes">How large the download is.</param>
/// <param name="InstalledTag">What is installed now, or empty.</param>
/// <param name="CanInstall">True when there is something worth fetching.</param>
public sealed record BypassReleaseView(
    string Tag,
    DateTimeOffset PublishedAtUtc,
    long SizeBytes,
    string InstalledTag,
    bool CanInstall);

/// <param name="Started">True when the bypass is up and stayed up.</param>
/// <param name="ReasonResourceKey">Why not, as a resource key. Empty on success.</param>
/// <param name="Detail">
/// What the tool itself said, unlocalized because it comes from the tool. Empty when it said
/// nothing.
/// </param>
public sealed record BypassStartView(string ReasonResourceKey, bool Started, string Detail);

/// <summary>The bypass, for the presentation layer.</summary>
public interface IBypassService
{
    string Folder { get; }

    bool IsInstalled { get; }

    IReadOnlyList<BypassStrategyView> Strategies();

    BypassStatusView Status();

    BypassStartView Start(string strategyId);

    bool Stop();

    Task<BypassReleaseView?> CheckAsync(CancellationToken cancellationToken = default);

    Task<string> InstallAsync(IProgress<double>? progress, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class BypassService : IBypassService
{
    private readonly IBypassStrategyCatalog _catalog;
    private readonly IBypassProcessController _process;
    private readonly IBypassReleaseInstaller _installer;

    /// <summary>
    /// The release found by the last check, kept so installing does not have to ask again — and so
    /// what gets installed is exactly what the user was shown and agreed to.
    /// </summary>
    private BypassRelease? _offered;

    public BypassService(
        IBypassStrategyCatalog catalog,
        IBypassProcessController process,
        IBypassReleaseInstaller installer)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
    }

    public string Folder => _catalog.RootDirectory;

    public bool IsInstalled => _catalog.IsInstalled;

    public IReadOnlyList<BypassStrategyView> Strategies() =>
        _catalog.Strategies()
            .Select(static strategy => new BypassStrategyView(strategy.Id, strategy.Name))
            .ToArray();

    public BypassStatusView Status()
    {
        var status = _process.Status();

        return new BypassStatusView(
            "Bypass_State_" + status.State,
            status.State == BypassState.Running,
            status.State != BypassState.Stopped,
            status.State == BypassState.ForeignRunning ? status.ExecutablePath : string.Empty);
    }

    /// <remarks>
    /// The strategy is resolved from the catalog rather than taken from the caller, so nothing can
    /// hand the process controller an arbitrary executable and argument list — the same rule the
    /// cursor and sound services follow, and it matters more here.
    /// </remarks>
    public BypassStartView Start(string strategyId)
    {
        var strategy = _catalog.Strategies()
            .FirstOrDefault(candidate => string.Equals(candidate.Id, strategyId, StringComparison.Ordinal));

        if (strategy is null)
        {
            return new BypassStartView("Bypass_Start_Missing", Started: false, string.Empty);
        }

        var report = _process.Start(strategy);

        return new BypassStartView(
            report.Started ? string.Empty : "Bypass_Start_" + report.Outcome,
            report.Started,
            report.Detail);
    }

    public bool Stop() => _process.Stop();

    public async Task<BypassReleaseView?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var check = await _installer.CheckAsync(cancellationToken).ConfigureAwait(false);
        _offered = check.Latest;

        if (check.Latest is null)
        {
            return null;
        }

        return new BypassReleaseView(
            check.Latest.Tag,
            check.Latest.PublishedAtUtc,
            check.Latest.SizeBytes,
            check.InstalledTag,
            check.UpdateAvailable);
    }

    /// <remarks>
    /// Installs only what a preceding check offered. Without one there is nothing the user has been
    /// shown and agreed to, and this downloads an executable that will run with administrator
    /// rights — not something to do on an unseen version.
    /// </remarks>
    /// <summary>
    /// Installs the release the screen offered, and answers with the resource key of what to say.
    /// </summary>
    /// <remarks>
    /// A key rather than a boolean, because the five ways this fails are five different things for
    /// the person to do about it. They all used to arrive as false and be reported as "the antivirus
    /// probably deleted the files during unpacking" — one plausible cause presented as the only one.
    /// </remarks>
    public async Task<string> InstallAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        if (_offered is not { } release)
        {
            return "Bypass_Install_NothingOffered";
        }

        var outcome = await _installer
            .InstallAsync(release, progress, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            BypassInstallOutcome.Installed => "Bypass_Installed",
            BypassInstallOutcome.NoDiskSpace => "Bypass_Install_NoSpace",
            BypassInstallOutcome.DownloadFailed => "Bypass_Install_DownloadFailed",
            BypassInstallOutcome.ArchiveUnreadable => "Bypass_Install_ArchiveUnreadable",
            BypassInstallOutcome.PayloadIncomplete => "Bypass_Install_Incomplete",
            BypassInstallOutcome.DriverLoaded => "Bypass_Install_DriverLoaded",
            _ => "Bypass_Install_FolderLocked",
        };
    }
}
