using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>
/// The first screen.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately close to empty, and it has now been emptied twice. On 2026-08-04 it lost capability
/// counters, a Windows build readout, deployment status, presets and an about card. On 2026-08-09 it
/// lost a list of recent changes with an undo beside each one, built the day before at the owner's
/// request and removed on sight: it read as clutter on the screen that is supposed to be the calm
/// one. That answer is on the Changes screen, which exists for it.
/// </para>
/// <para>
/// Two things survive both passes, and the reason is the same in each case — neither is the app
/// talking about itself. The recovery warning appears only when an unfinished change is blocking
/// every other screen, and without it a person meets that state as a click that refuses for no
/// stated reason. The community button is one glyph in a corner and is the only route to the project
/// from inside the app.
/// </para>
/// <para>
/// Before adding anything here again: the test this screen keeps failing is whether the thing is
/// something a person arrived wanting, or something the app wanted to say. Three separate attempts
/// have now been removed for landing on the wrong side of it.
/// </para>
/// </remarks>
public sealed partial class DashboardViewModel : ObservableObject
{
    /// <summary>
    /// The project's Discord, supplied by the owner. A literal rather than a setting because there
    /// is one, it does not vary per machine, and a link nothing can rewrite cannot be pointed
    /// somewhere else by anything the app happens to read.
    /// </summary>
    public const string CommunityUrl = "https://discord.gg/bJCWdzx4D6";

    private readonly IRecoveryState _recovery;
    private readonly ILocalizationService _text;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SafetyStatement { get; set; } = string.Empty;

    /// <summary>
    /// Shown when an earlier change never finished. Nothing can be applied until it is reconciled,
    /// so this is stated on the first screen rather than discovered on a failed click.
    /// </summary>
    [ObservableProperty]
    public partial string RecoveryNotice { get; set; } = string.Empty;

    public bool HasRecovery => !string.IsNullOrEmpty(RecoveryNotice);

    partial void OnRecoveryNoticeChanged(string value) => OnPropertyChanged(nameof(HasRecovery));

    [ObservableProperty]
    public partial string RecoveryActionLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRecovering { get; set; }

    /// <summary>Tooltip on the community button, which shows no text of its own.</summary>
    [ObservableProperty]
    public partial string CommunityTooltip { get; set; } = string.Empty;

    public DashboardViewModel(IRecoveryState recovery, ILocalizationService text)
    {
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <summary>
    /// Rolls the unfinished operation back. This is the only transition out of that state, and
    /// until it runs nothing else in the app can be applied at all.
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        if (IsRecovering)
        {
            return;
        }

        IsRecovering = true;
        try
        {
            var outcome = await _recovery.RecoverAsync(cancellationToken).ConfigureAwait(true);

            if (outcome.Failed > 0)
            {
                RecoveryNotice = string.Format(
                    CultureInfo.CurrentCulture,
                    _text.Get("Dashboard_RecoveryFailed"),
                    outcome.Failed,
                    outcome.FirstFailure);
                return;
            }

            await LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsRecovering = false;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Dashboard");
        SafetyStatement = _text.Get("App_Safety_Statement");
        RecoveryActionLabel = _text.Get("Dashboard_RecoveryAction");
        CommunityTooltip = _text.Get("Dashboard_CommunityAction");

        var pending = await _recovery.PendingCountAsync(cancellationToken).ConfigureAwait(true);
        RecoveryNotice = pending > 0
            ? string.Format(CultureInfo.CurrentCulture, _text.Get("Dashboard_RecoveryPending"), pending)
            : string.Empty;
    }
}
