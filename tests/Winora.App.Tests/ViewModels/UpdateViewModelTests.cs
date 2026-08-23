using Winora.App.Services;
using Winora.App.ViewModels;
using Xunit;

namespace Winora.App.Tests.ViewModels;

/// <summary>
/// The strip's outcome-to-message mapping: every way <see cref="IAppUpdateService.UpdateAsync"/> can
/// end must show its own message and never be folded into another's, and each failure must leave a
/// way forward rather than a dead end.
/// </summary>
public sealed class UpdateViewModelTests
{
    [Theory]
    [InlineData(AppUpdateOutcomeView.DownloadFailed, "Update_Failed_Download")]
    [InlineData(AppUpdateOutcomeView.Verification, "Update_Failed_Verification")]
    [InlineData(AppUpdateOutcomeView.Displaced, "Update_Failed_Displaced")]
    [InlineData(AppUpdateOutcomeView.SwapFailed, "Update_Failed_Swap")]
    [InlineData(AppUpdateOutcomeView.NotInstalled, "Update_Failed_Swap")]
    public async Task Act_maps_each_failure_outcome_to_its_own_message(
        AppUpdateOutcomeView outcome,
        string expectedResourceKey)
    {
        var update = new FakeUpdateService
        {
            IsInstalled = true,
            NextCheck = new AppUpdateReleaseView("9.9.9", "v9.9.9"),
        };
        var vm = Build(update);
        await vm.CheckCommand.ExecuteAsync(null);

        update.NextOutcome = outcome;
        await vm.ActCommand.ExecuteAsync(null);

        Assert.Equal(expectedResourceKey, vm.Message);

        // A dead end is the one thing every failure must not leave: the button comes back pointing
        // at the release page no matter which of these five ways the update failed.
        Assert.True(vm.IsActionVisible);
        Assert.Equal("Update_Action_Open", vm.ActionLabel);
    }

    /// <summary>
    /// Reported apart from every other outcome, per Task 4: telling somebody nothing changed while
    /// their program is missing from its own path is the one answer worse than saying nothing.
    /// </summary>
    [Fact]
    public async Task Displaced_is_never_collapsed_into_the_generic_swap_failure_message()
    {
        var update = new FakeUpdateService
        {
            IsInstalled = true,
            NextCheck = new AppUpdateReleaseView("9.9.9", "v9.9.9"),
            NextOutcome = AppUpdateOutcomeView.Displaced,
        };
        var vm = Build(update);
        await vm.CheckCommand.ExecuteAsync(null);

        await vm.ActCommand.ExecuteAsync(null);

        Assert.NotEqual("Update_Failed_Swap", vm.Message);
        Assert.Equal("Update_Failed_Displaced", vm.Message);
    }

    [Fact]
    public async Task Act_requests_a_restart_when_installing_succeeds_and_restart_succeeds()
    {
        var update = new FakeUpdateService
        {
            IsInstalled = true,
            NextCheck = new AppUpdateReleaseView("9.9.9", "v9.9.9"),
            NextOutcome = AppUpdateOutcomeView.Installed,
            RestartSucceeds = true,
        };
        var vm = Build(update);
        await vm.CheckCommand.ExecuteAsync(null);

        var restarted = false;
        vm.RestartRequested += (_, _) => restarted = true;

        await vm.ActCommand.ExecuteAsync(null);

        Assert.True(restarted);
        Assert.Equal("Update_Restarting", vm.Message);
    }

    /// <summary>
    /// The file is in place; only the hand-off failed. The screen must say to reopen it by hand
    /// rather than claim a restart is underway that never happens.
    /// </summary>
    [Fact]
    public async Task Act_reports_restart_failure_separately_from_a_successful_install()
    {
        var update = new FakeUpdateService
        {
            IsInstalled = true,
            NextCheck = new AppUpdateReleaseView("9.9.9", "v9.9.9"),
            NextOutcome = AppUpdateOutcomeView.Installed,
            RestartSucceeds = false,
        };
        var vm = Build(update);
        await vm.CheckCommand.ExecuteAsync(null);

        var restarted = false;
        vm.RestartRequested += (_, _) => restarted = true;

        await vm.ActCommand.ExecuteAsync(null);

        Assert.False(restarted);
        Assert.Equal("Update_Failed_Restart", vm.Message);
    }

    /// <summary>
    /// A copy running from wherever it was downloaded cannot replace itself: Act must send it to the
    /// release page instead of calling UpdateAsync on a file that was never offered up.
    /// </summary>
    [Fact]
    public async Task Act_opens_the_release_page_instead_of_updating_when_this_copy_is_not_installed()
    {
        var update = new FakeUpdateService
        {
            IsInstalled = false,
            NextCheck = new AppUpdateReleaseView("9.9.9", "v9.9.9"),
        };
        var vm = Build(update);
        await vm.CheckCommand.ExecuteAsync(null);

        var openedPage = false;
        vm.OpenPageRequested += (_, _) => openedPage = true;

        await vm.ActCommand.ExecuteAsync(null);

        Assert.True(openedPage);
        Assert.Equal("Update_NotInstalled", vm.Message);
        Assert.False(update.UpdateAsyncCalled);
    }

    /// <summary>
    /// A check that failed and a check that found nothing look the same from where the person sits.
    /// IAppUpdateService.CheckAsync already folds both into null; the ViewModel must not invent a
    /// difference on top of that, and must stay silent unless it was asked.
    /// </summary>
    [Fact]
    public async Task An_unprompted_check_that_finds_nothing_stays_silent()
    {
        var update = new FakeUpdateService { NextCheck = null };
        var vm = Build(update);

        await vm.StartupAsync();

        Assert.False(vm.IsBannerVisible);
        Assert.Equal(string.Empty, vm.Message);
    }

    [Fact]
    public async Task A_requested_check_that_finds_nothing_says_so()
    {
        var update = new FakeUpdateService { NextCheck = null };
        var vm = Build(update);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.IsBannerVisible);
        Assert.Equal("Update_UpToDate", vm.Message);
    }

    [Fact]
    public async Task Startup_clears_leftovers_before_checking()
    {
        var update = new FakeUpdateService { NextCheck = null };
        var vm = Build(update);

        await vm.StartupAsync();

        Assert.True(update.RemoveLeftoversCalled);
    }

    [Fact]
    public async Task Startup_does_nothing_in_the_packaged_build()
    {
        var update = new FakeUpdateService { NextCheck = null };
        var vm = Build(update, isPackaged: true);

        await vm.StartupAsync();

        Assert.False(update.RemoveLeftoversCalled);
        Assert.False(vm.IsBannerVisible);
    }

    private static UpdateViewModel Build(FakeUpdateService update, bool isPackaged = false) =>
        new(update, new FakeAppEnvironment(), new FakeDeploymentState(isPackaged), new FakeLocalizationService());

    private sealed class FakeUpdateService : IAppUpdateService
    {
        public bool IsInstalled { get; set; } = true;

        public AppUpdateReleaseView? NextCheck { get; set; }

        public AppUpdateOutcomeView NextOutcome { get; set; }

        public bool RestartSucceeds { get; set; } = true;

        public bool RemoveLeftoversCalled { get; private set; }

        public bool UpdateAsyncCalled { get; private set; }

        public void RemoveLeftovers() => RemoveLeftoversCalled = true;

        public Task<AppUpdateReleaseView?> CheckAsync(
            string currentVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(NextCheck);

        public Task<AppUpdateOutcomeView> UpdateAsync(
            IProgress<double>? progress,
            CancellationToken cancellationToken = default)
        {
            UpdateAsyncCalled = true;
            return Task.FromResult(NextOutcome);
        }

        public bool Restart() => RestartSucceeds;
    }

    private sealed class FakeAppEnvironment : IAppEnvironment
    {
        public string Version => "1.0.0";

        public bool IsElevated => false;

        public string StorageRoot => string.Empty;
    }

    private sealed class FakeDeploymentState(bool isPackaged) : IDeploymentState
    {
        public bool IsPackaged { get; } = isPackaged;

        public bool CanApplyChanges => !IsPackaged;

        public string? ApplyBlockReasonKey => null;
    }

    private sealed class FakeLocalizationService : ILocalizationService
    {
        public bool IsAvailable => true;

        // Returns the key itself: the assertions above check which key was chosen, not its Russian
        // text, which is already covered by ResourceKeyTests.Every_requested_resource_key_is_defined.
        public string Get(string resourceKey) => resourceKey;
    }
}
