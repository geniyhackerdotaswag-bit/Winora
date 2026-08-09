using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>One bypass strategy, as a selectable row.</summary>
public sealed partial class BypassStrategyViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
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

    public BypassViewModel(IBypassService bypass, ILocalizationService text)
    {
        _bypass = bypass ?? throw new ArgumentNullException(nameof(bypass));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

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

        // The previous choice survives a reinstall where the strategy still exists, so updating
        // does not silently move the user onto a different one.
        Selected = previous is null
            ? null
            : Strategies.FirstOrDefault(s => string.Equals(s.Id, previous, StringComparison.Ordinal));
    }
}
