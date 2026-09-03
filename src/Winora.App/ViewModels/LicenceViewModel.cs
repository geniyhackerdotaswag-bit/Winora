using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;
using Winora.Core.Licence;

namespace Winora.App.ViewModels;

/// <summary>
/// The subscription screen: enter a key, see what it bought.
/// </summary>
/// <remarks>
/// Every refusal gets its own sentence. The site can say four different noes — the key is wrong,
/// its time has run out, every machine slot is taken, or it could not be reached — and each sends a
/// person somewhere different: to their typing, to the site to renew, to the cabinet to free a
/// slot, or to their network. One "не удалось" would send them nowhere, which is the failure this
/// screen exists to avoid.
/// </remarks>
public sealed partial class LicenceViewModel : ObservableObject
{
    private readonly ILicenceService _licence;
    private readonly ILocalizationService _text;
    private readonly TimeProvider _time;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    /// <summary>What the key field holds. Bound two ways: the person types into it.</summary>
    [ObservableProperty]
    public partial string Key { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PromoCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string KeyPlaceholder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PromoPlaceholder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ActivateLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RefreshLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ForgetLabel { get; set; } = string.Empty;

    /// <summary>The plan and the date, when there is a subscription to describe.</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    /// <summary>The last thing that happened, in words. Empty when nothing has.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>True when a key is stored here, whatever its state.</summary>
    [ObservableProperty]
    public partial bool HasLicence { get; set; }

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>
    /// Поле ключа показывается, пока настоящей подписки нет.
    /// </summary>
    /// <remarks>
    /// Во время пробы — тоже: человек на пробе как раз тот, кто вот-вот купит,
    /// и прятать от него поле значило бы прятать кассу.
    /// </remarks>
    public bool ShowEntry => !HasLicence || Current.IsTrial;

    /// <summary>Что известно о подписке прямо сейчас.</summary>
    public LicenceState Current { get; private set; } = LicenceState.None;

    public bool CanActivate => !IsBusy && LicenceKey.IsWellFormed(Key);

    public LicenceViewModel(ILicenceService licence, ILocalizationService text, TimeProvider? time = null)
    {
        _licence = licence ?? throw new ArgumentNullException(nameof(licence));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _time = time ?? TimeProvider.System;
    }

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnHasLicenceChanged(bool value) => OnPropertyChanged(nameof(ShowEntry));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanActivate));

    partial void OnKeyChanged(string value) => OnPropertyChanged(nameof(CanActivate));

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Licence");
        Description = _text.Get("Licence_Description");
        KeyPlaceholder = _text.Get("Licence_KeyPlaceholder");
        PromoPlaceholder = _text.Get("Licence_PromoPlaceholder");
        ActivateLabel = _text.Get("Licence_Activate");
        RefreshLabel = _text.Get("Licence_Refresh");
        ForgetLabel = _text.Get("Licence_Forget");

        Describe(_licence.Current);

        if (!_licence.IsConfigured)
        {
            StatusMessage = _text.Get("Licence_NotConfigured");
            return;
        }

        // Only when it is due. Opening this screen should not cost a request every time.
        if (HasLicence)
        {
            await CheckAsync(force: false, cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var result = await _licence
                .ActivateAsync(Key, PromoCode, cancellationToken)
                .ConfigureAwait(true);

            Describe(_licence.Current);

            if (result.Succeeded)
            {
                // The field is cleared on success and only then. A rejected key stays put so the
                // person can see what they typed and fix one letter rather than all sixteen.
                Key = string.Empty;
                PromoCode = string.Empty;

                StatusMessage = result.BonusDays > 0
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        _text.Get("Licence_ActivatedWithPromo"),
                        result.BonusDays)
                    : _text.Get("Licence_Activated");
                return;
            }

            StatusMessage = MessageFor(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        CheckAsync(force: true, cancellationToken);

    public void Forget()
    {
        _licence.Forget();
        Describe(LicenceState.None);
        StatusMessage = _text.Get("Licence_Forgotten");
    }

    private async Task CheckAsync(bool force, CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _licence.RefreshAsync(force, cancellationToken).ConfigureAwait(true);
            Describe(_licence.Current);

            // Silence on a routine confirmation. A banner saying "всё в порядке" every time the
            // screen opens is noise, and noise is what people stop reading before the one time it
            // matters.
            StatusMessage = result.Succeeded && !force ? string.Empty : MessageFor(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Describe(LicenceState state)
    {
        Current = state;
        HasLicence = state.Exists;
        OnPropertyChanged(nameof(ShowEntry));

        if (!state.Exists)
        {
            Summary = string.Empty;
            return;
        }

        var now = _time.GetUtcNow();

        // Вечная — до всего остального: у неё дата стоит в 9999 году, и «действует
        // до 31 декабря 9999, осталось дней: 2913864» никому ничего не сообщает.
        if (state.IsPerpetual)
        {
            Summary = _text.Get("Licence_Forever");
            return;
        }

        if (state.IsTrial)
        {
            Summary = state.IsActive(now)
                ? string.Format(CultureInfo.CurrentCulture, _text.Get("Licence_TrialLeft"), state.DaysLeft(now))
                : _text.Get("Licence_TrialOver");
            return;
        }

        var plan = _text.Get(PlanKeyFor(state.Plan));

        Summary = state.IsActive(now)
            ? string.Format(
                CultureInfo.CurrentCulture,
                _text.Get("Licence_Active"),
                plan,
                state.ExpiresUtc!.Value.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture),
                state.DaysLeft(now))
            : string.Format(
                CultureInfo.CurrentCulture,
                _text.Get("Licence_Ended"),
                state.ExpiresUtc!.Value.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture));
    }

    /// <summary>
    /// Maps a plan identifier from the site to its resource key.
    /// </summary>
    /// <remarks>
    /// Written out rather than composed from the identifier, so a plan added on the site without a
    /// word here shows the identifier itself instead of an empty space. Ugly is recoverable; blank
    /// is a screen that looks broken.
    /// </remarks>
    private static string PlanKeyFor(string plan) => plan switch
    {
        "week" => "Licence_Plan_Week",
        "month" => "Licence_Plan_Month",
        "quarter" => "Licence_Plan_Quarter",
        "half-year" => "Licence_Plan_HalfYear",
        "year" => "Licence_Plan_Year",
        "lifetime" => "Licence_Plan_Forever",
        _ => "Licence_Plan_Unknown",
    };

    private string MessageFor(LicenceResult result) => result.Outcome switch
    {
        LicenceOutcome.Activated => _text.Get("Licence_Activated"),
        LicenceOutcome.Confirmed => _text.Get("Licence_Confirmed"),
        LicenceOutcome.Malformed => _text.Get("Licence_Malformed"),
        LicenceOutcome.Rejected => _text.Get("Licence_Rejected"),
        LicenceOutcome.Expired => _text.Get("Licence_Expired"),
        LicenceOutcome.DeviceLimit => string.Format(
            CultureInfo.CurrentCulture,
            _text.Get("Licence_DeviceLimit"),
            result.DeviceLimit),
        LicenceOutcome.OtherMachine => _text.Get("Licence_OtherMachine"),
        LicenceOutcome.Trial => _text.Get("Licence_Confirmed"),
        LicenceOutcome.TrialUsed => _text.Get("Licence_TrialOver"),
        LicenceOutcome.Unreachable => _text.Get("Licence_Unreachable"),
        LicenceOutcome.NotConfigured => _text.Get("Licence_NotConfigured"),
        _ => throw new ArgumentOutOfRangeException(
            nameof(result),
            result.Outcome,
            "The licence outcome has no message."),
    };
}
