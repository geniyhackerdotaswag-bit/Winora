using Winora.App.Services;
using Winora.System.Updates;
using Xunit;

namespace Winora.App.Tests.Services;

/// <summary>
/// The translation between Winora.System.Updates and the presentation layer's own vocabulary --
/// the seam <c>SolutionStructureTests.ViewModels_never_reference_infrastructure_or_system_directly</c>
/// exists to force.
/// </summary>
public sealed class AppUpdateServiceTests
{
    private static AppRelease Release(string tag, string notes, long sizeBytes) => new(
        new Version(9, 9, 9),
        tag,
        notes,
        "https://example.invalid/Winora.exe",
        "https://example.invalid/Winora.exe.sha256",
        sizeBytes,
        DateTimeOffset.UnixEpoch);

    /// <summary>
    /// Review finding (Important 3): AppRelease.Notes and SizeBytes were parsed by the feed and
    /// asserted on there, then dropped at this exact seam -- AppUpdateReleaseView carried only
    /// Version and Tag. Both must reach the presentation layer, or the strip has no way to show them.
    /// </summary>
    [Fact]
    public async Task CheckAsync_carries_notes_and_size_to_the_presentation_layer()
    {
        var release = Release("v9.9.9", "Заметки к релизу.", 92_274_688);
        var service = new AppUpdateService(
            new FakeFeed(release),
            new FakeUpdater(),
            new FakeLocation(isInstalled: true));

        var found = await service.CheckAsync("1.0.0");

        Assert.NotNull(found);
        Assert.Equal("Заметки к релизу.", found!.Notes);
        Assert.Equal(92_274_688, found.SizeBytes);
    }

    /// <summary>
    /// Review finding (Minor 1): UpdateAsync used to return NotInstalled both when nothing had been
    /// offered yet and when the updater reported the copy was not at the installed path -- two
    /// different facts collapsed into one name, which the view model then mapped to the same
    /// swap-failure message. Calling UpdateAsync with no preceding CheckAsync must not be reported the
    /// same way as a copy the updater refused to touch.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_without_a_preceding_check_is_reported_apart_from_a_copy_that_is_not_installed()
    {
        var service = new AppUpdateService(
            new FakeFeed(null),
            new FakeUpdater(),
            new FakeLocation(isInstalled: true));

        // No CheckAsync call: nothing has been offered, and _offered is still null.
        var outcome = await service.UpdateAsync(null);

        Assert.Equal(AppUpdateOutcomeView.NoUpdateOffered, outcome);
    }

    /// <summary>The other half of Minor 1: a real "not installed" from the updater keeps its own name.</summary>
    [Fact]
    public async Task UpdateAsync_reports_a_copy_the_updater_refused_as_NotInstalled_not_NoUpdateOffered()
    {
        var release = Release("v9.9.9", string.Empty, 1024);
        var updater = new FakeUpdater { NextOutcome = UpdateOutcome.NotInstalled };
        var service = new AppUpdateService(new FakeFeed(release), updater, new FakeLocation(isInstalled: true));

        // A preceding check offers the release, so _offered is set this time.
        await service.CheckAsync("1.0.0");

        var outcome = await service.UpdateAsync(null);

        Assert.Equal(AppUpdateOutcomeView.NotInstalled, outcome);
        Assert.NotEqual(AppUpdateOutcomeView.NoUpdateOffered, outcome);
    }

    private sealed class FakeFeed(AppRelease? release, bool reached = true) : IAppReleaseFeed
    {
        public Task<AppReleaseLookup> LatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(reached ? AppReleaseLookup.Answered(release) : AppReleaseLookup.Unreachable);
    }

    private sealed class FakeUpdater : IAppUpdater
    {
        public UpdateOutcome NextOutcome { get; set; } = UpdateOutcome.Installed;

        public Task<UpdateOutcome> UpdateAsync(
            AppRelease release,
            IProgress<double>? progress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(NextOutcome);

        public void RemoveLeftovers()
        {
        }

        public bool Restart() => true;
    }

    private sealed class FakeLocation(bool isInstalled) : IAppInstallLocation
    {
        public string CurrentExecutablePath => @"C:\fake\Winora.exe";

        public string InstalledDirectory => @"C:\fake";

        public string InstalledExecutablePath => @"C:\fake\Winora.exe";

        public bool IsInstalled => isInstalled;
    }
}
