using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;
using Winora.Core.Bypass;

namespace Winora.App.ViewModels;

/// <summary>One bypass strategy, as a selectable row.</summary>
public sealed partial class BypassStrategyViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>What happened last time this one was started, in words.</summary>
    /// <remarks>
    /// The only thing on this screen the program knows and the person does not. Everything else —
    /// which strategy suits this network, what the names mean — it cannot know either.
    /// </remarks>
    [ObservableProperty]
    public partial string OutcomeText { get; set; } = string.Empty;

    /// <summary>True for the strategy running right now.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>True when this machine has been told this one does not help.</summary>
    [ObservableProperty]
    public partial bool HasFailed { get; set; }
}

/// <summary>
/// Running the bypass from Flowseal's <c>zapret-discord-youtube</c>.
/// </summary>
/// <remarks>
/// <para>
/// The one module in Winora that sits outside the change pipeline, by the owner's decision on
/// 2026-08-07. Starting a process has no previous value to back up and nothing to roll back to, so
/// plan-backup-verify-undo has nothing to work with here.
/// </para>
/// <para>
/// Two things that pipeline gave are kept anyway, because they are not ceremony. The state on screen
/// is read from the running process list every second, never remembered — the bypass outlives the
/// app, so a cached flag would say "off" while the network was still being filtered. And nothing is
/// downloaded or installed without the user seeing the release tag, its date and its size first: it
/// is an executable that will run with administrator rights.
/// </para>
/// </remarks>
public sealed partial class BypassViewModel : ObservableObject
{
    private readonly IBypassService _bypass;
    private readonly ILocalizationService _text;
    private readonly IBypassHistory _history;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    /// <summary>Says plainly what this runs and with what rights.</summary>
    [ObservableProperty]
    public partial string Disclosure { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string ForeignWarning { get; set; } = string.Empty;

    public bool HasForeignWarning => !string.IsNullOrEmpty(ForeignWarning);

    partial void OnForeignWarningChanged(string value) => OnPropertyChanged(nameof(HasForeignWarning));

    [ObservableProperty]
    public partial string StartLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StopLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CheckLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InstallLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StrategiesHeading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Folder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenFolderLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReleaseNote { get; set; } = string.Empty;

    public bool HasReleaseNote => !string.IsNullOrEmpty(ReleaseNote);

    partial void OnReleaseNoteChanged(string value) => OnPropertyChanged(nameof(HasReleaseNote));

    [ObservableProperty]
    public partial bool CanInstall { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    [ObservableProperty]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    public partial string NotInstalledMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial BypassStrategyViewModel? Selected { get; set; }

    partial void OnSelectedChanged(BypassStrategyViewModel? value)
    {
        foreach (var strategy in Strategies)
        {
            strategy.IsSelected = ReferenceEquals(strategy, value);
        }

        OnPropertyChanged(nameof(CanStart));
    }

    /// <summary>A strategy has to be chosen, and nothing may already be filtering traffic.</summary>
    public bool CanStart => Selected is not null && !IsRunning && !IsBusy && !HasForeignWarning;

    public bool CanStop => IsRunning && !IsBusy;

    public ObservableCollection<BypassStrategyViewModel> Strategies { get; } = [];

    public BypassViewModel(IBypassService bypass, ILocalizationService text, IBypassHistory history)
    {
        _bypass = bypass ?? throw new ArgumentNullException(nameof(bypass));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    /// <summary>
    /// The question that only a person can answer, shown after a strategy has been started.
    /// </summary>
    /// <remarks>
    /// Winora can see that <c>winws.exe</c> is alive. It cannot see whether Discord opened, and no
    /// probe would tell it, because what counts as working is whatever the person came here to do.
    /// So it asks, once, and only after something was actually started.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsAskingVerdict { get; set; }

    [ObservableProperty]
    public partial string VerdictQuestion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string VerdictYes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string VerdictNo { get; set; } = string.Empty;

    /// <summary>
    /// What the search has left to offer, or a sentence saying it has run out.
    /// </summary>
    /// <remarks>
    /// Every strategy having failed is a real answer and worth stating. Pointing at the first one
    /// again, as though it were new, would send somebody round the list a second time.
    /// </remarks>
    [ObservableProperty]
    public partial string ExhaustedNote { get; set; } = string.Empty;

    public bool IsExhausted => !string.IsNullOrEmpty(ExhaustedNote);

    partial void OnExhaustedNoteChanged(string value) => OnPropertyChanged(nameof(IsExhausted));

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Title = _text.Get("Nav_Bypass");
        Subtitle = _text.Get("Bypass_Subtitle");
        Disclosure = _text.Get("Bypass_Disclosure");
        StartLabel = _text.Get("Bypass_Start");
        StopLabel = _text.Get("Bypass_Stop");
        CheckLabel = _text.Get("Bypass_Check");
        InstallLabel = _text.Get("Bypass_Install");
        StrategiesHeading = _text.Get("Bypass_StrategiesHeading");
        NotInstalledMessage = _text.Get("Bypass_NotInstalled");
        // The real path, and it is no longer shown anywhere — the folder button opens it. Printing
        // it across the screen put the account name, often a real name, into every screenshot.
        Folder = _bypass.Folder;
        OpenFolderLabel = _text.Get("Bypass_OpenFolder");
        VerdictQuestion = _text.Get("Bypass_Verdict_Question");
        VerdictYes = _text.Get("Bypass_Verdict_Yes");
        VerdictNo = _text.Get("Bypass_Verdict_No");

        LoadStrategies();
        RefreshStatus();

        return Task.CompletedTask;
    }

    /// <summary>Re-reads what is actually running. Called on a timer by the page.</summary>
    public void RefreshStatus()
    {
        var status = _bypass.Status();

        StateText = _text.Get(status.StateResourceKey);
        IsRunning = status.IsOurs;

        ForeignWarning = status.ForeignPath.Length > 0
            ? string.Format(
                CultureInfo.CurrentCulture,
                _text.Get("Bypass_ForeignRunning"),
                PathDisplay.Redact(status.ForeignPath))
            : string.Empty;

        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
    }

    public void Select(BypassStrategyViewModel strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        Selected = strategy;
    }

    public void Start()
    {
        if (!CanStart || Selected is not { } strategy)
        {
            return;
        }

        var report = _bypass.Start(strategy.Id);

        // The tool's own words are appended when it left any. They are not translated: they come
        // from the tool, and a paraphrase would lose the file name that says what actually went
        // wrong.
        StatusMessage = report.Started
            ? string.Empty
            : string.Concat(
                _text.Get(report.ReasonResourceKey),
                report.Detail.Length > 0 ? " " + report.Detail : string.Empty);

        if (report.Started)
        {
            // Recorded at the start, unjudged, so a run that is abandoned without an answer still
            // shows up as "tried" rather than vanishing from the search.
            _history.Started(strategy.Id);
            RefreshHistory();
            IsAskingVerdict = true;
        }

        RefreshStatus();
    }

    public void Stop()
    {
        if (!CanStop)
        {
            return;
        }

        StatusMessage = _bypass.Stop() ? string.Empty : _text.Get("Bypass_StopFailed");
        RefreshStatus();
    }

    /// <summary>
    /// Looks up the newest release and reports what it is, without fetching anything.
    /// </summary>
    public async Task CheckAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));

        try
        {
            var release = await _bypass.CheckAsync().ConfigureAwait(true);

            if (release is null)
            {
                ReleaseNote = string.Empty;
                CanInstall = false;
                StatusMessage = _text.Get("Bypass_CheckFailed");
                return;
            }

            // Tag, date and size before anything is downloaded: this is an executable that will
            // later run with administrator rights, so the user decides on the specifics.
            ReleaseNote = string.Format(
                CultureInfo.CurrentCulture,
                _text.Get(release.CanInstall ? "Bypass_ReleaseAvailable" : "Bypass_ReleaseCurrent"),
                release.Tag,
                release.PublishedAtUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
                release.SizeBytes / (1024d * 1024d));

            CanInstall = release.CanInstall;
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
        }
    }

    public async Task InstallAsync()
    {
        if (IsBusy || !CanInstall)
        {
            return;
        }

        // Replacing files under a running bypass would leave it running from deleted files.
        if (_bypass.Status().IsAnythingRunning)
        {
            StatusMessage = _text.Get("Bypass_StopBeforeInstalling");
            return;
        }

        IsBusy = true;
        Progress = 0;
        StatusMessage = string.Empty;

        try
        {
            var progress = new Progress<double>(value => Progress = value * 100);
            var installed = await _bypass.InstallAsync(progress).ConfigureAwait(true);

            StatusMessage = installed
                ? _text.Get("Bypass_Installed")
                : _text.Get("Bypass_InstallFailed");

            if (installed)
            {
                CanInstall = false;
                ReleaseNote = string.Empty;
                LoadStrategies();
            }
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            RefreshStatus();
        }
    }

    private void LoadStrategies()
    {
        var previous = Selected?.Id;
        Strategies.Clear();

        foreach (var strategy in _bypass.Strategies())
        {
            Strategies.Add(new BypassStrategyViewModel { Id = strategy.Id, Name = strategy.Name });
        }

        IsInstalled = _bypass.IsInstalled;
        RefreshHistory();

        // The previous choice survives a reinstall where the strategy still exists, so updating
        // does not silently move the user onto a different one. With nothing chosen, the search
        // picks up where it left off rather than at the top of the list.
        Selected =
            (previous is null
                ? null
                : Strategies.FirstOrDefault(s => string.Equals(s.Id, previous, StringComparison.Ordinal)))
            ?? Suggested();
    }

    /// <summary>Which strategy the record says to try next, if any.</summary>
    private BypassStrategyViewModel? Suggested()
    {
        var published = Strategies.Select(s => s.Id).ToArray();
        var next = BypassAttemptRules.NextToTry(published, _history.Attempts);

        return next is null
            ? null
            : Strategies.FirstOrDefault(s => string.Equals(s.Id, next, StringComparison.Ordinal));
    }

    /// <summary>Puts what this machine has learned onto the rows.</summary>
    private void RefreshHistory()
    {
        var attempts = _history.Attempts;

        foreach (var row in Strategies)
        {
            var latest = BypassAttemptRules.Latest(attempts, row.Id);

            row.HasFailed = latest?.Outcome == BypassOutcome.Failed;
            row.OutcomeText = latest is null
                ? _text.Get("Bypass_Outcome_Untried")
                : string.Format(
                    CultureInfo.CurrentCulture,
                    _text.Get(latest.Outcome switch
                    {
                        BypassOutcome.Worked => "Bypass_Outcome_Worked",
                        BypassOutcome.Failed => "Bypass_Outcome_Failed",
                        _ => "Bypass_Outcome_Started",
                    }),
                    latest.WhenUtc.ToLocalTime().ToString("d MMMM", CultureInfo.CurrentCulture));
        }

        ExhaustedNote =
            Strategies.Count > 0 && BypassAttemptRules.NextToTry(
                Strategies.Select(s => s.Id).ToArray(),
                attempts) is null
                    ? _text.Get("Bypass_Exhausted")
                    : string.Empty;
    }

    /// <summary>The person says it worked. The search is over until it stops working.</summary>
    public void Worked() => Settle(BypassOutcome.Worked);

    /// <summary>
    /// The person says it did not. The strategy is struck off and the next one is put up.
    /// </summary>
    public void DidNotWork()
    {
        Settle(BypassOutcome.Failed);
        Selected = Suggested();
        OnPropertyChanged(nameof(CanStart));
    }

    private void Settle(BypassOutcome outcome)
    {
        IsAskingVerdict = false;

        if (Selected is { } strategy)
        {
            _history.Settle(strategy.Id, outcome);
            RefreshHistory();
        }
    }
}
