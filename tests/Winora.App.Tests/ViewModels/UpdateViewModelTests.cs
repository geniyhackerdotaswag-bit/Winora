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
    [InlineData(AppUpdateOutcomeView.NoUpdateOffered, "Update_Failed_NoOffer")]
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
        // at the release page no matter which of these ways the update failed.
        Assert.True(vm.IsActionVisible);
        Assert.Equal("Update_Action_Open", vm.ActionLabel);

        // Review finding (Minor 3): every one of these is something the person has to act on, not
        // routine status, and the strip must read that way regardless of which failure it is.
        Assert.True(vm.IsFailure);
    }

    /// <summary>
    /// Review finding (Important 2): the button's label and its action used to be decided by two
    /// different things -- Fail() set the label to "Открыть страницу", but Act() still asked
    /// IAppUpdateService.IsInstalled, which stays true after Displaced because the copy is still at
    /// the installed path; only its executable was renamed aside. A second press therefore fell
    /// through to a fresh download, which on the Displaced path truncates the one verified rescue
    /// copy the failure message told the person to keep. This asserts the button now does what it
    /// says: a second press opens the page and never calls UpdateAsync again.
    /// </summary>
    [Fact]
    public async Task Pressing_the_button_again_after_a_failure_opens_the_page_instead_of_retrying()
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
        Assert.Equal(1, update.UpdateAsyncCallCount);
        Assert.Equal("Update_Failed_Displaced", vm.Message);

        var openedPage = false;
        vm.OpenPageRequested += (_, _) => openedPage = true;

        await vm.ActCommand.ExecuteAsync(null);

        Assert.True(openedPage);
        // Not called a second time: the copy that update would have downloaded into is exactly the
        // rescue file Update_Failed_Displaced told the person to keep.
        Assert.Equal(1, update.UpdateAsyncCallCount);
        // The failure message stays on screen; IsInstalled is still true here (the copy did not
        // move), so this must not be overwritten with Update_NotInstalled either.
        Assert.Equal("Update_Failed_Displaced", vm.Message);
    }

    /// <summary>
    /// The other half of Important 2: an offer that has not failed must still install normally, not
    /// be routed to the release page by mistake.
    /// </summary>
    [Fact]
    public async Task A_normal_offer_still_installs()
    {
        var update = new FakeUpdateService
        {
            IsInstalled = true,
            NextCheck = new AppUpdateReleaseView("9.9.9", "v9.9.9"),
            NextOutcome = AppUpdateOutcomeView.Installed,
        };
        var vm = Build(update);
        await vm.CheckCommand.ExecuteAsync(null);

        var openedPage = false;
        vm.OpenPageRequested += (_, _) => openedPage = true;

        await vm.ActCommand.ExecuteAsync(null);

        Assert.True(update.UpdateAsyncCalled);
        Assert.False(openedPage);
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

    /// <summary>
    /// Not having asked is not the same as having nothing to report.
    /// </summary>
    /// <remarks>
    /// These were one case until 2026-08-26. With the repository closed the feed answers 404 to
    /// every check, so the program told everybody who pressed the button that they had the latest
    /// version, having never once managed to ask — the single lie it told about itself.
    /// </remarks>
    [Fact]
    public async Task A_requested_check_that_could_not_ask_says_that_instead()
    {
        var update = new FakeUpdateService { NextCheck = null, Reached = false };
        var vm = Build(update);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.IsBannerVisible);
        Assert.Equal("Update_Unreachable", vm.Message);
        Assert.False(vm.IsActionVisible);
    }

    /// <summary>
    /// The background check at startup stays silent either way: an unprompted "could not check" is
    /// a complaint about a question nobody put.
    /// </summary>
    [Fact]
    public async Task A_silent_check_that_could_not_ask_says_nothing()
    {
        var update = new FakeUpdateService { NextCheck = null, Reached = false };
        var vm = Build(update);

        await vm.StartupAsync();

        Assert.False(vm.IsBannerVisible);
        Assert.Equal(string.Empty, vm.Message);
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

        // Review finding (Minor 4): IsDownloading used to not exist, and the progress bar was bound
        // to IsBusy, which is also true here -- a bar sitting at zero for the length of a check reads
        // as a stalled download rather than the quick lookup this actually is.
        Assert.False(vm.IsDownloading);

        update.PendingCheck.SetResult(null);
        await check;

        Assert.False(vm.IsDownloading);
    }

    /// <summary>
    /// The other half of Minor 4: the progress bar must actually appear while a download is under
    /// way, not just stay hidden during a check.
    /// </summary>
    [Fact]
    public async Task IsDownloading_is_true_only_while_a_download_is_in_flight()
    {
        var update = new FakeUpdateService
        {
            IsInstalled = true,
            NextCheck = new AppUpdateReleaseView("9.9.9", "v9.9.9"),
            PendingUpdate = new TaskCompletionSource<AppUpdateOutcomeView>(),
        };
        var vm = Build(update);
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.False(vm.IsDownloading);

        var act = vm.ActCommand.ExecuteAsync(null);
        Assert.True(vm.IsDownloading);

        update.PendingUpdate.SetResult(AppUpdateOutcomeView.Installed);
        await act;

        Assert.False(vm.IsDownloading);
    }

    /// <summary>
    /// Review finding (Important 3): AppRelease.Notes and SizeBytes reached AppUpdateReleaseView and
    /// then went nowhere -- the strip started an 88 MB download with no idea what changed or how
    /// large it was. Empty notes (a release published without a body) must not leave the message
    /// ending in a dangling ". " with nothing after it.
    /// </summary>
    [Fact]
    public async Task Empty_release_notes_leave_no_dangling_punctuation()
    {
        var update = new FakeUpdateService
        {
            IsInstalled = true,
            NextCheck = new AppUpdateReleaseView("9.9.9", "v9.9.9", Notes: "", SizeBytes: 92_274_688),
        };
        var vm = Build(update);

        await vm.CheckCommand.ExecuteAsync(null);

        // FakeLocalizationService echoes the key for everything but Update_Settings_Version, so with
        // no notes to append the message is exactly the (unformatted) template -- nothing tacked on.
        Assert.Equal("Update_Available", vm.Message);
        Assert.DoesNotContain(". ", vm.Message);
    }

    /// <summary>Long release notes are cut to a sensible length rather than shown in full.</summary>
    [Fact]
    public async Task Long_release_notes_are_truncated_with_an_ellipsis()
    {
        var longNotes = new string('a', 200);
        var update = new FakeUpdateService
        {
            IsInstalled = true,
            NextCheck = new AppUpdateReleaseView("9.9.9", "v9.9.9", Notes: longNotes, SizeBytes: 92_274_688),
        };
        var vm = Build(update);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.StartsWith("Update_Available. ", vm.Message);
        Assert.EndsWith("…", vm.Message);
        // Shorter than "the base message, a separator, and the notes in full" would have been.
        Assert.True(vm.Message.Length < "Update_Available. ".Length + longNotes.Length);
    }

    /// <summary>
    /// The strip is one line of prose, so what goes into it must be prose.
    /// </summary>
    /// <remarks>
    /// Seen on the first real release, 26 August 2026: the whole body GitHub generated was
    /// "**Full Changelog**: https://github.com/…/commits/v0.4.0", and the banner printed it
    /// verbatim, asterisks and URL and all. Nobody reads a link out of a notification strip, and
    /// the markup is an artefact of where the text came from rather than anything anybody wrote.
    /// </remarks>
    [Theory]
    [InlineData("**Full Changelog**: https://github.com/a/b/commits/v0.4.0", "")]
    [InlineData("Full Changelog: https://github.com/a/b/commits/v0.4.0", "")]
    [InlineData("**Плитки на Главной**", "Плитки на Главной")]
    [InlineData("`RouteRegistry` теперь один", "RouteRegistry теперь один")]
    [InlineData("## Что нового\n\nПлитки на Главной", "Что нового")]
    public async Task Release_notes_reach_the_strip_as_prose(string notes, string expected)
    {
        var update = new FakeUpdateService
        {
            NextCheck = new AppUpdateReleaseView("9.9.9", "v9.9.9", notes, SizeBytes: 1),
        };
        var vm = Build(update);

        await vm.CheckCommand.ExecuteAsync(null);

        var shown = vm.Message.Replace("Update_Available", string.Empty, StringComparison.Ordinal).Trim();
        Assert.Equal(expected.Length == 0 ? string.Empty : ". " + expected, shown);
    }

    /// <summary>
    /// Review finding (Important 4): MainWindow used to write UpdateBar.Message and UpdateBar.IsOpen
    /// directly for the first-run install offer, bypassing the one source of truth Dismiss()
    /// established. These two methods are what it calls instead, and they must distinguish "the copy
    /// landed but could not be handed off" from "the copy never happened" the same way
    /// Update_Failed_Restart already distinguishes those two outcomes on the update path.
    /// </summary>
    [Fact]
    public void ReportInstallRestartFailed_says_the_copy_succeeded_and_reads_as_routine()
    {
        var vm = Build(new FakeUpdateService());

        vm.ReportInstallRestartFailed();

        Assert.Equal("Install_Failed_Restart", vm.Message);
        Assert.True(vm.IsBannerVisible);
        Assert.False(vm.IsActionVisible);
        Assert.False(vm.IsFailure);
    }

    [Fact]
    public void ReportInstallFailed_says_the_copy_never_happened_and_reads_as_a_failure()
    {
        var vm = Build(new FakeUpdateService());

        vm.ReportInstallFailed();

        Assert.Equal("Install_Failed", vm.Message);
        Assert.True(vm.IsBannerVisible);
        Assert.False(vm.IsActionVisible);
        Assert.True(vm.IsFailure);
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

    /// <summary>A fresh check does not inherit the red severity of whatever the previous attempt left behind.</summary>
    [Fact]
    public async Task IsFailure_is_cleared_by_the_next_check()
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
        Assert.True(vm.IsFailure); // sanity: the failure is what set it

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.False(vm.IsFailure);
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
    /// background check at startup). These properties read straight from ILocalizationService with
    /// no state of their own, so this is a guard against a copy-pasted resource key drifting from
    /// what the property actually reads.
    ///
    /// The card carried three more lines until 2026-08-24 — what the check does, which version is
    /// installed, and the last answer — and the owner had them removed. The answer has a home
    /// already: the strip at the top of the window.
    /// </summary>
    [Fact]
    public void The_settings_facing_properties_read_their_own_resource_keys()
    {
        var vm = Build(new FakeUpdateService());

        Assert.Equal("Update_Settings_Heading", vm.SettingsHeading);
        Assert.Equal("Update_Settings_Check", vm.SettingsCheckLabel);
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

        /// <summary>Whether the last check managed to reach the feed. True unless a test says not.</summary>
        public bool Reached { get; set; } = true;

        public AppUpdateReleaseView? NextCheck { get; set; }

        public AppUpdateOutcomeView NextOutcome { get; set; }

        public bool RestartSucceeds { get; set; } = true;

        public bool RemoveLeftoversCalled { get; private set; }

        public bool UpdateAsyncCalled { get; private set; }

        public int UpdateAsyncCallCount { get; private set; }

        public int CheckAsyncCallCount { get; private set; }

        /// <summary>
        /// When set, CheckAsync returns this task instead of completing immediately — the hook the
        /// race test uses to hold a check open while a second one is attempted.
        /// </summary>
        public TaskCompletionSource<AppUpdateReleaseView?>? PendingCheck { get; set; }

        /// <summary>
        /// When set, UpdateAsync returns this task instead of completing immediately — used to
        /// observe IsDownloading while a download is still in flight.
        /// </summary>
        public TaskCompletionSource<AppUpdateOutcomeView>? PendingUpdate { get; set; }

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
            UpdateAsyncCallCount++;
            return PendingUpdate?.Task ?? Task.FromResult(NextOutcome);
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
