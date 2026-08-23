using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>
/// The one strip at the top of the window that says a new version exists.
/// </summary>
/// <remarks>
/// <para>
/// Silent when there is nothing to say. A check that failed and a check that found nothing look the
/// same from where the person sits, and inventing a difference would fill the top of the window with
/// notices about the health of somebody else's API.
/// </para>
/// <para>
/// Switched off entirely in the packaged build. An MSIX app lives under
/// <c>C:\Program Files\WindowsApps</c>, which is protected by the operating system and cannot be
/// written to; a strip promising an update that could never be installed is worse than no strip.
/// </para>
/// <para>
/// Talks only to <see cref="IAppUpdateService" />, never to the release feed and updater directly:
/// this project's inner layers are off limits to a ViewModel, a rule
/// <c>SolutionStructureTests</c> enforces by reading the source text.
/// </para>
/// </remarks>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly IAppUpdateService _update;
    private readonly IAppEnvironment _environment;
    private readonly IDeploymentState _deployment;
    private readonly ILocalizationService _text;

    private AppUpdateReleaseView? _found;

    public UpdateViewModel(
        IAppUpdateService update,
        IAppEnvironment environment,
        IDeploymentState deployment,
        ILocalizationService text)
    {
        _update = update ?? throw new ArgumentNullException(nameof(update));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <remarks>
    /// Partial properties, not fields: MVVMTK0045 requires this form in WinUI 3 so the CsWinRT
    /// generators can emit the WinRT marshalling code.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsBannerVisible { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ActionLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsActionVisible { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>Where to send somebody whose copy cannot update itself.</summary>
    public string ReleasePageUrl =>
        _found is null
            ? "https://github.com/geniyhackerdotaswag-bit/Winora/releases/latest"
            : $"https://github.com/geniyhackerdotaswag-bit/Winora/releases/tag/{_found.Tag}";

    /// <summary>Raised when the process should end because a newer one has been started.</summary>
    public event EventHandler? RestartRequested;

    /// <summary>Raised when the person should be sent to the release page in a browser.</summary>
    public event EventHandler? OpenPageRequested;

    /// <summary>
    /// Closes the strip because the person closed it, not because there is nothing to say.
    /// </summary>
    /// <remarks>
    /// Does not touch <see cref="IsBusy" /> or cancel anything: closing a notice is not cancelling
    /// an update that is already under way, and the next check is free to reopen the strip on its
    /// own account.
    /// </remarks>
    public void Dismiss() => IsBannerVisible = false;

    /// <summary>
    /// The one check made without being asked, at startup.
    /// </summary>
    /// <remarks>
    /// Also the moment the debris of a previous update is cleared: the displaced file is no longer
    /// running by now, so this is the first point at which it can actually be deleted.
    /// </remarks>
    public async Task StartupAsync()
    {
        if (_deployment.IsPackaged)
        {
            return;
        }

        _update.RemoveLeftovers();
        await CheckAsync(announceNothing: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task Check() => CheckAsync(announceNothing: true);

    private async Task CheckAsync(bool announceNothing)
    {
        if (_deployment.IsPackaged || IsBusy)
        {
            return;
        }

        // Set for the whole check, the same way BypassViewModel.CheckAsync guards itself: without
        // this, the startup check and a person pressing "check now" race to overwrite _found,
        // Message, IsBannerVisible and IsActionVisible, and whichever finishes last wins silently.
        IsBusy = true;

        // Only when they asked. An unprompted "checking…" is a notice about nothing, the same
        // reasoning that keeps an unprompted "up to date" silent below.
        if (announceNothing)
        {
            Message = _text.Get("Update_Checking");
            IsActionVisible = false;
            IsBannerVisible = true;
        }

        try
        {
            // Whether the check failed, found nothing, or found nothing newer are indistinguishable
            // from here: IAppUpdateService.CheckAsync already folds all three into null, because a
            // check that failed is not something the person can act on.
            _found = await _update.CheckAsync(_environment.Version).ConfigureAwait(true);

            if (_found is null)
            {
                IsActionVisible = false;

                // Only when they asked. An unprompted "you are up to date" is a notice about nothing.
                Message = announceNothing ? _text.Get("Update_UpToDate") : string.Empty;
                IsBannerVisible = announceNothing;
                return;
            }

            Message = string.Format(CultureInfo.CurrentCulture, _text.Get("Update_Available"), _found.Version);

            // A copy running from wherever it was downloaded cannot replace itself: that file was
            // never offered up. It is sent to the page instead.
            ActionLabel = _update.IsInstalled
                ? _text.Get("Update_Action_Install")
                : _text.Get("Update_Action_Open");

            IsActionVisible = true;
            IsBannerVisible = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Act()
    {
        if (_found is null || IsBusy)
        {
            return;
        }

        if (!_update.IsInstalled)
        {
            Message = _text.Get("Update_NotInstalled");
            OpenPageRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        IsBusy = true;
        IsActionVisible = false;
        Progress = 0;
        Message = _text.Get("Update_Downloading");

        try
        {
            var outcome = await _update
                .UpdateAsync(new Progress<double>(value => Progress = value))
                .ConfigureAwait(true);

            switch (outcome)
            {
                case AppUpdateOutcomeView.Installed:
                    Message = _text.Get("Update_Restarting");
                    if (_update.Restart())
                    {
                        RestartRequested?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        Message = _text.Get("Update_Failed_Restart");
                    }

                    break;

                case AppUpdateOutcomeView.DownloadFailed:
                    Fail("Update_Failed_Download");
                    break;

                case AppUpdateOutcomeView.Verification:
                    Fail("Update_Failed_Verification");
                    break;

                case AppUpdateOutcomeView.Displaced:
                    // The program was moved aside and could not be put back: it is not sitting where
                    // it was, and folding this into the generic "all is as it was" message would be
                    // exactly the falsehood AppUpdateOutcomeView.Displaced exists to prevent.
                    Fail("Update_Failed_Displaced");
                    break;

                case AppUpdateOutcomeView.SwapFailed:
                case AppUpdateOutcomeView.NotInstalled:
                default:
                    Fail("Update_Failed_Swap");
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Fail(string resourceKey)
    {
        Message = _text.Get(resourceKey);

        // The button comes back saying "open the page": whatever went wrong here, the release is
        // still downloadable by hand, and a dead end would be the wrong place to leave somebody.
        ActionLabel = _text.Get("Update_Action_Open");
        IsActionVisible = true;
    }
}
