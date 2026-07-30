using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.App.Navigation;
using Winora.App.Services;
using Winora.Core.Changes;
using Winora.Core.Contracts;

namespace Winora.App.ViewModels;

/// <summary>
/// Carries one change from dry run through confirmation, apply, verification, and rollback. Review,
/// applying, and result are separate routes observing this single instance, so the plan the user
/// confirmed is provably the plan that runs.
/// </summary>
public sealed partial class ChangeSessionViewModel : ObservableObject
{
    private readonly ChangeCoordinator _coordinator;
    private readonly ConfirmationAuthority _confirmation;
    private readonly IDurableOperationJournal _journal;
    private readonly IBackupRepository _backups;
    private readonly INavigationService _navigation;
    private readonly ILocalizationService _text;

    private IOperation? _operation;

    [ObservableProperty]
    public partial ChangePlan? Plan { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TargetId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentValueText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProposedValueText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Facts { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DocumentationUri { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlanDigest { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool CanRollback { get; set; }

    /// <summary>False when this process cannot hold the mutation lease, with the reason stated.</summary>
    [ObservableProperty]
    public partial bool CanApply { get; set; }

    [ObservableProperty]
    public partial string ApplyBlockReason { get; set; } = string.Empty;

    public bool HasApplyBlockReason => !string.IsNullOrEmpty(ApplyBlockReason);

    partial void OnApplyBlockReasonChanged(string value) => OnPropertyChanged(nameof(HasApplyBlockReason));

    public ChangeSessionViewModel(
        ChangeCoordinator coordinator,
        ConfirmationAuthority confirmation,
        IDurableOperationJournal journal,
        IBackupRepository backups,
        IDeploymentState deployment,
        INavigationService navigation,
        ILocalizationService text)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _text = text ?? throw new ArgumentNullException(nameof(text));

        ArgumentNullException.ThrowIfNull(deployment);
        CanApply = deployment.CanApplyChanges;
        ApplyBlockReason = deployment.ApplyBlockReasonKey is { } key ? _text.Get(key) : string.Empty;
    }

    public void BeginReview(IOperation operation, ChangePlan plan)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(plan);

        _operation = operation;
        Plan = plan;
        CanRollback = false;
        ResultMessage = string.Empty;
        ResultDetail = string.Empty;

        var step = plan.Steps[0];
        Title = plan.Title;
        TargetId = step.Target.TargetId;
        CurrentValueText = _text.Get($"Value_{step.CurrentValue.Text}");
        ProposedValueText = _text.Get($"Value_{step.ProposedValue.Text}");
        DocumentationUri = plan.Documentation.ToString();
        PlanDigest = plan.Digest[..16];

        Facts = string.Join(
            "    ",
            $"{_text.Get("Fact_Risk")}: {_text.Get($"Risk_{plan.Risk}")}",
            $"{_text.Get("Fact_Privilege")}: {_text.Get($"Privilege_{plan.Privilege}")}",
            $"{_text.Get("Fact_Rollback")}: {_text.Get($"Rollback_{plan.Rollback}")}",
            $"{_text.Get("Fact_Restart")}: {_text.Get($"Restart_{plan.Restart}")}",
            $"{_text.Get("Fact_Backup")}: {_text.Get($"Backup_{plan.Backup}")}");
    }

    /// <summary>Leaves the review without calling anything. Cancel must never touch the system.</summary>
    [RelayCommand]
    private void Cancel()
    {
        Plan = null;
        _operation = null;
        _navigation.NavigateTo(RouteKeys.Themes);
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (Plan is null || _operation is null || IsBusy || !CanApply)
        {
            return;
        }

        IsBusy = true;
        _navigation.NavigateTo(RouteKeys.Applying);
        try
        {
            // A fresh token per attempt: TryAuthorize consumes it, so a retry cannot replay one.
            var token = _confirmation.Confirm(Plan);

            // The lease and journal writes are synchronous file work; keep them off the UI thread.
            var result = await Task.Run(() =>
                _coordinator.ApplyAsync(_operation, Plan, token, CancellationToken.None).AsTask())
                .ConfigureAwait(true);

            Present(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (Plan is null || _operation is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var receipt = await Task.Run(() => ReadBackupReceiptAsync(Plan, CancellationToken.None))
                .ConfigureAwait(true);
            if (receipt is null)
            {
                ResultMessage = _text.Get("Result_BackupUnavailable");
                CanRollback = false;
                _navigation.NavigateTo(RouteKeys.ResultFailure);
                return;
            }

            var rollback = RollbackPlan.Create(
                Guid.NewGuid(),
                Plan,
                receipt,
                Plan.Steps[^1].ResultFingerprint);

            var token = _confirmation.Confirm(rollback);
            var result = await Task.Run(() =>
                _coordinator.RollbackAsync(_operation, rollback, token, CancellationToken.None).AsTask())
                .ConfigureAwait(true);

            Present(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Rollback restores from the verified backup the apply recorded, so the identifiers come from the
    /// durable journal rather than from anything the UI held in memory.
    /// </summary>
    private async Task<BackupReceipt?> ReadBackupReceiptAsync(ChangePlan plan, CancellationToken ct)
    {
        var boundary = await _journal.ReadVerifiedBoundaryAsync(plan.PlanId, ct).ConfigureAwait(false);
        if (boundary?.Facts.BackupId is not { } backupId ||
            boundary.Facts.BackupDigest is not { } backupDigest)
        {
            return null;
        }

        return await _backups
            .ReadAndVerifyOperationBackupAsync(plan, backupId, backupDigest, ct)
            .ConfigureAwait(false);
    }

    private void Present(CoordinatorResult result)
    {
        ResultMessage = _text.Get(CoordinatorDispositionPresentation.ResourceKeyFor(result.Disposition));
        ResultDetail = result.Detail ?? string.Empty;
        CanRollback = result.Disposition is CoordinatorDisposition.Completed
            or CoordinatorDisposition.VerificationFailed;
        _navigation.NavigateTo(CoordinatorDispositionPresentation.RouteFor(result.Disposition));
    }
}
