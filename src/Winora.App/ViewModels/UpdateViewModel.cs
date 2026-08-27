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

    /// <summary>
    /// Whether pressing the strip's button should send the person to the release page instead of
    /// starting a download.
    /// </summary>
    /// <remarks>
    /// Review finding (Important 2): the button used to be routed purely by
    /// <see cref="IAppUpdateService.IsInstalled"/>, a path comparison that is still true after
    /// <see cref="AppUpdateOutcomeView.Displaced"/> -- the copy is still sitting at the installed
    /// path, only its executable has been renamed aside. Pressing "Открыть страницу" after any
    /// <see cref="Fail"/> would therefore fall through to a fresh download, truncating the kept
    /// rescue copy on the Displaced path. This field is set alongside <see cref="ActionLabel"/> so
    /// the button's action can never drift from what it says.
    /// </remarks>
    private bool _mustOpenPage;

    /// <summary>How much of the release notes fit on the strip before they are cut off.</summary>
    private const int NotesMaxLength = 160;

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

    /// <summary>
    /// Whether a download is under way right now, as opposed to a check.
    /// </summary>
    /// <remarks>
    /// Review finding (Minor 4): the progress bar used to be bound to <see cref="IsBusy"/>, which is
    /// also true for the instant background check at startup and for a requested "Проверить" -- both
    /// opened the strip with a bar sitting at zero for the whole check, which reads as a stalled
    /// download rather than the quick lookup it actually is. This is true only while
    /// <see cref="Act"/> is downloading.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>
    /// Whether the strip is currently showing something the person has to act on, as opposed to
    /// routine status.
    /// </summary>
    /// <remarks>
    /// Review finding (Minor 3): the strip's <c>Severity</c> used to be a static "Informational" in
    /// XAML regardless of what <see cref="Message"/> said, including for
    /// <c>Update_Failed_Displaced</c> -- the one message that is not routine status. MainWindow reads
    /// this to choose the InfoBar's severity, the same way it already reads every other piece of the
    /// strip's state from here rather than deciding anything about it on its own.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsFailure { get; set; }

    /// <summary>Where to send somebody whose copy cannot update itself.</summary>
    public string ReleasePageUrl =>
        _found is null
            ? "https://github.com/geniyhackerdotaswag-bit/Winora/releases/latest"
            : $"https://github.com/geniyhackerdotaswag-bit/Winora/releases/tag/{_found.Tag}";

    // The four members below back the "Обновления" section on the Settings page (spec section 6:
    // a background check at startup plus a "Проверить" button). They read straight from
    // ILocalizationService/IAppEnvironment rather than being cached in fields, the same way
    // ReleasePageUrl above does: nothing here changes after construction, so there is nothing a
    // cache would save except the one call CultureInfo.CurrentUICulture would need repeating for.

    /// <summary>The heading over the section.</summary>
    public string SettingsHeading => _text.Get("Update_Settings_Heading");

    /// <summary>Label for the "check now" button, bound to <see cref="CheckCommand" />.</summary>
    public string SettingsCheckLabel => _text.Get("Update_Settings_Check");

    /// <summary>
    /// Whether the Settings section has anything to offer at all.
    /// </summary>
    /// <remarks>
    /// False in the packaged build. <see cref="IDeploymentState.IsPackaged" /> switches off the
    /// whole update surface — the strip at the top of the window included — and a "Проверить" button
    /// that could only ever report a version nobody can install is the exact empty promise that
    /// design refuses everywhere else.
    /// </remarks>
    public bool IsSettingsSectionVisible => !_deployment.IsPackaged;

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

        // A fresh check starts a fresh read of the strip. Whatever the previous attempt left behind
        // -- a failure's red severity, a button pointed at the release page -- does not belong to
        // this one until this one says otherwise.
        IsFailure = false;

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
            _found = await _update.CheckAsync(_environment.Version).ConfigureAwait(true);

            if (_found is null)
            {
                IsActionVisible = false;

                // Only when they asked. An unprompted "you are up to date" is a notice about
                // nothing, and an unprompted "could not check" is worse — it is a complaint about
                // a question nobody put.
                //
                // But somebody who pressed the button is owed the truth about which of the two it
                // was. Saying "you have the latest version" without having managed to ask is the
                // one lie this program tells about itself, and with the repository closed it told
                // it on every single check.
                Message = announceNothing
                    ? _text.Get(_update.Reached ? "Update_UpToDate" : "Update_Unreachable")
                    : string.Empty;
                IsBannerVisible = announceNothing;
                return;
            }

            // Spec section 6: the strip must show the version, the release notes and "Обновить" --
            // one press used to start an 88 MB download with no idea what changed or how large it
            // was. The size is converted to megabytes here rather than in a resource format
            // specifier, because a resource file has no way to spell "divide by 1024 * 1024".
            var megabytes = _found.SizeBytes / (1024d * 1024d);
            var summary = string.Format(
                CultureInfo.CurrentCulture,
                _text.Get("Update_Available"),
                _found.Version,
                megabytes);
            var notes = SummarizeNotes(_found.Notes);

            // Release notes are free text from GitHub and are often empty. Appending an empty
            // sentence would leave the summary ending in ". " with nothing after it -- the dangling
            // punctuation this is written to avoid -- so the separator is only added when there is
            // something to separate.
            Message = notes.Length == 0 ? summary : summary + ". " + notes;

            // A copy running from wherever it was downloaded cannot replace itself: that file was
            // never offered up. It is sent to the page instead. The flag drives Act() directly, so
            // the button's action can never point somewhere its own label does not.
            _mustOpenPage = !_update.IsInstalled;
            ActionLabel = _mustOpenPage
                ? _text.Get("Update_Action_Open")
                : _text.Get("Update_Action_Install");

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

        // Review finding (Important 2): this used to ask _update.IsInstalled directly, which is a
        // path comparison that stays true after Displaced -- the copy is still at the installed
        // path, only its executable has been renamed aside. Asking _mustOpenPage instead means the
        // button does exactly what its own label promised, whether that was decided by CheckAsync
        // finding an uninstalled copy or by a later Fail().
        if (_mustOpenPage)
        {
            if (!_update.IsInstalled)
            {
                Message = _text.Get("Update_NotInstalled");
            }

            OpenPageRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        IsBusy = true;
        IsDownloading = true;
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

                case AppUpdateOutcomeView.NoUpdateOffered:
                    // Review finding (Minor 1): distinct from NotInstalled below -- nothing has been
                    // offered yet, which says nothing about where this copy is sitting. Reachable
                    // only if Act() is ever called without a preceding CheckAsync finding something,
                    // which the guard at the top of this method already prevents in the UI itself.
                    Fail("Update_Failed_NoOffer");
                    break;

                case AppUpdateOutcomeView.NoDiskSpace:
                    // Named in megabytes rather than left as "not enough room". The number is the
                    // whole of what somebody can act on, and this refusal happens before the
                    // download precisely so that there is still something able to say it.
                    //
                    // The button stays a retry rather than turning into "open the page", which is
                    // what every other failure here does. Nothing was downloaded and nothing was
                    // touched, so pressing again after clearing some room is both safe and exactly
                    // the right thing to do — and downloading the same file by hand would need the
                    // same room this just said was missing.
                    Message = string.Format(
                        CultureInfo.CurrentCulture,
                        _text.Get("Update_Failed_DiskSpace"),
                        (_found?.RequiredBytes ?? 0) / (1024d * 1024d));

                    IsFailure = true;
                    IsActionVisible = true;
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
            IsDownloading = false;
        }
    }

    private void Fail(string resourceKey)
    {
        Message = _text.Get(resourceKey);

        // The button comes back saying "open the page": whatever went wrong here, the release is
        // still downloadable by hand, and a dead end would be the wrong place to leave somebody.
        // _mustOpenPage is set alongside the label so a second press cannot fall through to a fresh
        // download -- see the field's remarks for the destructive case this used to allow.
        _mustOpenPage = true;
        ActionLabel = _text.Get("Update_Action_Open");
        IsActionVisible = true;
        IsFailure = true;
    }

    /// <summary>
    /// Reports that the first-run install offer copied the program but could not hand off to the
    /// new process.
    /// </summary>
    /// <remarks>
    /// Review finding (Important 4): MainWindow used to write UpdateBar.Message and UpdateBar.IsOpen
    /// directly for this, bypassing the one source of truth Dismiss() established for the strip, and
    /// it used to say "Не удалось скопировать" for this exact case even though the copy had
    /// succeeded -- only starting the new process failed. Update_Failed_Restart already has the
    /// right words for the same situation on the update path; this is its install-time twin. Left at
    /// informational, the same as Update_Failed_Restart: the program is exactly where it should be,
    /// and reopening it by hand is the only thing left to do.
    /// </remarks>
    public void ReportInstallRestartFailed()
    {
        Message = _text.Get("Install_Failed_Restart");
        IsActionVisible = false;
        IsFailure = false;
        IsBannerVisible = true;
    }

    /// <summary>Reports that the first-run install offer could not copy the program at all.</summary>
    /// <remarks>See <see cref="ReportInstallRestartFailed"/> for the case this is paired with.</remarks>
    public void ReportInstallFailed()
    {
        Message = _text.Get("Install_Failed");
        IsActionVisible = false;
        IsFailure = true;
        IsBannerVisible = true;
    }

    /// <summary>
    /// Shortens release notes to a length that fits the strip, breaking on a word boundary rather
    /// than mid-word, and marking the cut with an ellipsis.
    /// </summary>
    /// <remarks>
    /// Empty notes come back empty rather than as an ellipsis on their own, so the caller can tell
    /// "nothing to show" apart from "something was cut" without a separate check.
    /// </remarks>
    private static string SummarizeNotes(string notes)
    {
        var trimmed = Prose(notes);

        if (trimmed.Length <= NotesMaxLength)
        {
            return trimmed;
        }

        var cut = trimmed.LastIndexOf(' ', NotesMaxLength - 1);
        var shortened = cut > 0 ? trimmed[..cut] : trimmed[..NotesMaxLength];

        return shortened.TrimEnd() + "…";
    }

    /// <summary>
    /// Reduces a release body to the one line of prose the strip can carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The body is whatever GitHub holds: Markdown somebody wrote, or notes it generated. On the
    /// first real release, 26 August 2026, the entire body was
    /// <c>**Full Changelog**: https://…/commits/v0.4.0</c>, and the strip printed it as it stood —
    /// asterisks, colon and URL. Nobody reads a link out of a notification, and the markup is an
    /// artefact of where the text came from rather than anything anybody meant to say.
    /// </para>
    /// <para>
    /// Only the first paragraph is taken. A release body is a document; this is a strip above a
    /// window, and the first paragraph is where anybody writing notes puts the summary.
    /// </para>
    /// <para>
    /// Deliberately not a Markdown parser. Stripping the four marks that actually turn up in
    /// release notes is the whole job, and a parser would be a dependency and a class of bug over
    /// text that is thrown away after a hundred and sixty characters.
    /// </para>
    /// </remarks>
    private static string Prose(string notes)
    {
        var paragraph = notes
            .ReplaceLineEndings("\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static block => !IsChangelogLink(block));

        if (paragraph is null)
        {
            return string.Empty;
        }

        var lines = paragraph
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !IsChangelogLink(line))
            .Select(static line => line.TrimStart('#', '-', '*', '>', ' '));

        return string.Join(' ', lines)
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    /// <summary>
    /// The line GitHub appends to every generated body, which is a URL and nothing else.
    /// </summary>
    /// <remarks>
    /// Matched on the address rather than on the words, so it is caught whatever language the
    /// account's interface is set to.
    /// </remarks>
    private static bool IsChangelogLink(string line) =>
        line.Contains("/commits/", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("/compare/", StringComparison.OrdinalIgnoreCase);
}
