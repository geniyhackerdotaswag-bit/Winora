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

    /// <summary>
    /// Review finding (Important 1): CheckAsync used to read IsBusy without ever setting it, so the
    /// startup check and a person pressing "check now" could race to overwrite _found, Message,
    /// IsBannerVisible and IsActionVisible — whichever finished last would win silently, and a found
    /// update could flip back to "up to date" or the reverse. IAppUpdateService.CheckAsync is made to
    /// hang here so the second call is guaranteed to land while the first is still in flight.
    /// </summary>
    [Fact]
    public async Task A_check_in_flight_blocks_a_second_check_from_starting()
    {
        var update = new FakeUpdateService
        {
            PendingCheck = new TaskCompletionSource<AppUpdateReleaseView?>(),
        };
        var vm = Build(update);

        // The command executes synchronously up to its first incomplete await, so by the time this
        // line returns, IsBusy is already true and the underlying task is not yet finished.
        var first = vm.CheckCommand.ExecuteAsync(null);

        // Must return immediately: IsBusy already guards it before this ever reaches
        // IAppUpdateService.CheckAsync a second time. If it did not, awaiting it here — before the
        // first check is ever unblocked — would deadlock the test.
        var second = vm.CheckCommand.ExecuteAsync(null);
        await second;

        Assert.Equal(1, update.CheckAsyncCallCount);

        update.PendingCheck.SetResult(new AppUpdateReleaseView("9.9.9", "v9.9.9"));
        await first;

        Assert.Equal(1, update.CheckAsyncCallCount);
        Assert.False(vm.IsBusy);
    }

    /// <summary>
    /// Review finding (Important 1, second half): the resource key was defined but never referenced
    /// anywhere. A check the person asked for now shows it while in flight — the startup check stays
    /// silent, per the class remarks: an unprompted "checking…" is a notice about nothing too.
    /// </summary>
    [Fact]
    public async Task A_requested_check_shows_the_checking_message_while_in_flight()
    {
        var update = new FakeUpdateService
        {
            PendingCheck = new TaskCompletionSource<AppUpdateReleaseView?>(),
        };
        var vm = Build(update);

        var check = vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.IsBusy);
        Assert.True(vm.IsBannerVisible);
        Assert.Equal("Update_Checking", vm.Message);

        update.PendingCheck.SetResult(null);
        await check;
    }

    /// <summary>
    /// Review finding (Important 2): the InfoBar's own close button only closes the control; nothing
    /// told the view model, so MainWindow's handler — which re-asserts UpdateBar.IsOpen from
    /// IsBannerVisible on every PropertyChanged — put the banner right back on the next notification.
    /// Dismiss() is what MainWindow now calls from the close button, and it has to make the view
    /// model the source of truth: hidden, and still hidden after something unrelated changes.
    /// </summary>
    [Fact]
    public async Task Dismiss_leaves_the_banner_hidden_through_a_later_unrelated_property_change()
    {
        var update = new FakeUpdateService
        {
            IsInstalled = true,
            NextCheck = new AppUpdateReleaseView("9.9.9", "v9.9.9"),
        };
        var vm = Build(update);
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.IsBannerVisible); // sanity: the check is what opened it

        vm.Dismiss();
        Assert.False(vm.IsBannerVisible);

        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        // Stands in for "a check now button exists" per the review: Act() changes IsBusy,
        // IsActionVisible, Progress and Message, but never IsBannerVisible — the same shape of
        // traffic that would have silently reopened the strip before this fix.
        update.NextOutcome = AppUpdateOutcomeView.DownloadFailed;
        await vm.ActCommand.ExecuteAsync(null);

        Assert.Contains(nameof(UpdateViewModel.Message), changedProperties);
        Assert.False(vm.IsBannerVisible);
    }

    /// <summary>Dismiss() only ever hides the strip. It must never reach into an update in progress.</summary>
    [Fact]
    public void Dismiss_does_not_cancel_or_touch_anything_else()
    {
        var update = new FakeUpdateService();
        var vm = Build(update);

        vm.Dismiss();

        Assert.False(vm.IsBannerVisible);
        Assert.False(update.UpdateAsyncCalled);
    }

    /// <summary>
    /// The section added for spec section 6 (a "check now" button in Settings, alongside the
    /// background check at startup). These properties read straight from
    /// ILocalizationService/IAppEnvironment with no state of their own, so this is mostly a guard
    /// against a copy-pasted resource key drifting from what the property actually reads, and
    /// against the version's string.Format silently dropping its argument.
    /// </summary>
    [Fact]
    public void The_settings_facing_properties_read_their_own_resource_keys()
    {
        var vm = Build(new FakeUpdateService());

        Assert.Equal("Update_Settings_Heading", vm.SettingsHeading);
        Assert.Equal("Update_Settings_Description", vm.SettingsDescription);
        Assert.Equal("Update_Settings_Check", vm.SettingsCheckLabel);

        // FakeLocalizationService returns a real "{0}" template for this one key specifically, so a
        // dropped format argument would show up here rather than being masked by the key echo the
        // other assertions above rely on.
        Assert.Equal("v1.0.0", vm.SettingsVersionLabel);
    }

    [Fact]
    public void The_settings_section_is_visible_outside_the_packaged_build()
    {
        Assert.True(Build(new FakeUpdateService(), isPackaged: false).IsSettingsSectionVisible);
    }

    /// <summary>
    /// IsPackaged switches off the whole update surface. A "Проверить" button that could only ever
    /// report a version nobody here is able to install is the exact empty promise the design refuses
    /// everywhere else — see UpdateViewModel's own class remarks.
    /// </summary>
    [Fact]
    public void The_settings_section_is_unavailable_in_the_packaged_build()
    {
        Assert.False(Build(new FakeUpdateService(), isPackaged: true).IsSettingsSectionVisible);
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

        public int CheckAsyncCallCount { get; private set; }

        /// <summary>
        /// When set, CheckAsync returns this task instead of completing immediately — the hook the
        /// race test uses to hold a check open while a second one is attempted.
        /// </summary>
        public TaskCompletionSource<AppUpdateReleaseView?>? PendingCheck { get; set; }

        public void RemoveLeftovers() => RemoveLeftoversCalled = true;

        public Task<AppUpdateReleaseView?> CheckAsync(
            string currentVersion,
            CancellationToken cancellationToken = default)
        {
            CheckAsyncCallCount++;
            return PendingCheck?.Task ?? Task.FromResult(NextCheck);
        }

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
        // The one exception is the version label, which is the only key UpdateViewModel actually
        // formats with an argument — a real "{0}" template here is what lets a dropped format
        // argument show up as a test failure instead of being hidden by the key-echo behaviour.
        public string Get(string resourceKey) =>
            resourceKey == "Update_Settings_Version" ? "v{0}" : resourceKey;
    }
}
