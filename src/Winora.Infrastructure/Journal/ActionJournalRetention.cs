using Winora.Core.Contracts;
using Winora.Core.Journal;

namespace Winora.Infrastructure.Journal;

public static class ActionJournalRetentionPolicy
{
    public static IReadOnlyList<ActionJournalEntry> SelectExpiredEvents(
        IReadOnlyList<ActionJournalEntry> events,
        IReadOnlySet<Guid> linkedChangeOperationIds,
        DateTimeOffset nowUtc,
        TimeSpan maximumAge,
        int maximumEventCount)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(linkedChangeOperationIds);
        ActionJournalSchema.ValidateTimestamp(nowUtc, nameof(nowUtc));
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maximumEventCount);
        foreach (var entry in events)
        {
            ActionJournalSchema.ValidatePersisted(entry);
        }

        if (events.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() != events.Count)
        {
            throw new InvalidDataException(
                "Retention requires a verified action-journal snapshot without duplicates.");
        }

        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        var cutoff = nowUtc - maximumAge;
        foreach (var entry in events.Where(entry =>
                     entry.TimestampUtc < cutoff &&
                     !IsProtected(entry, linkedChangeOperationIds)))
        {
            selectedIds.Add(entry.EventId);
        }

        var survivorCount = events.Count - selectedIds.Count;
        var overLimit = survivorCount - maximumEventCount;
        if (overLimit > 0)
        {
            foreach (var entry in events
                         .Where(entry =>
                             !selectedIds.Contains(entry.EventId) &&
                             !IsProtected(entry, linkedChangeOperationIds))
                         .OrderBy(entry => entry.TimestampUtc)
                         .ThenBy(entry => entry.EventId, StringComparer.Ordinal)
                         .Take(overLimit))
            {
                selectedIds.Add(entry.EventId);
            }
        }

        return Array.AsReadOnly(events
            .Where(entry => selectedIds.Contains(entry.EventId))
            .OrderBy(entry => entry.TimestampUtc)
            .ThenBy(entry => entry.EventId, StringComparer.Ordinal)
            .ToArray());
    }

    private static bool IsProtected(
        ActionJournalEntry entry,
        IReadOnlySet<Guid> linkedChangeOperationIds) =>
        linkedChangeOperationIds.Contains(entry.OperationId) &&
        entry.Status is
            ActionJournalStatus.Failed or
            ActionJournalStatus.RecoveryRequired or
            ActionJournalStatus.RollbackFailed;
}

internal sealed class ActionJournalRetentionCoordinator
{
    internal const int ReservedDecisionEventCount = 2;

    private readonly IActionJournal _actionJournal;
    private readonly DurableRetentionJournal _lifecycle;
    private readonly IRetentionArtifactStore _artifacts;
    private readonly TimeProvider _timeProvider;
    private readonly IRetentionMaintenanceFaultInjector? _faultInjector;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    internal ActionJournalRetentionCoordinator(
        IActionJournal actionJournal,
        DurableRetentionJournal lifecycle,
        IRetentionArtifactStore artifacts,
        TimeProvider? timeProvider = null,
        IRetentionMaintenanceFaultInjector? faultInjector = null)
    {
        _actionJournal = actionJournal ?? throw new ArgumentNullException(nameof(actionJournal));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _faultInjector = faultInjector;
    }

    internal async ValueTask<RetentionMaintenanceResult> RunAsync(
        IMutationLeaseHandle lease,
        ActionJournalRetentionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(request);
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RetentionTransactionBoundary boundary;
            var resumed = true;
            try
            {
                boundary = await _lifecycle.ReadAsync(
                    lease.OperationId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                resumed = false;
                var selection = await _artifacts.CaptureAsync(
                    request,
                    GetUtcNow(),
                    ReservedDecisionEventCount,
                    cancellationToken).ConfigureAwait(false);
                await RequireCurrentLeaseAsync(lease).ConfigureAwait(false);
                await _artifacts.VerifyLinkedStateAsync(
                    selection.LinkedState,
                    cancellationToken).ConfigureAwait(false);
                boundary = await _lifecycle.CreateApprovedAsync(
                    lease.OperationId,
                    lease,
                    request,
                    selection,
                    cancellationToken).ConfigureAwait(false);
            }

            return await DriveAsync(
                boundary,
                lease,
                resumed,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    internal async ValueTask<RetentionMaintenanceResult> ResumeAsync(
        IMutationLeaseHandle lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var boundary = await _lifecycle.ReadAsync(
                lease.OperationId,
                cancellationToken).ConfigureAwait(false);
            return await DriveAsync(
                boundary,
                lease,
                resumed: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async ValueTask<RetentionMaintenanceResult> DriveAsync(
        RetentionTransactionBoundary boundary,
        IMutationLeaseHandle lease,
        bool resumed,
        CancellationToken cancellationToken)
    {
        if (boundary.State == RetentionLifecycleState.Completed)
        {
            return Result(boundary, resumed);
        }

        boundary = await _lifecycle.ClaimAsync(
            boundary,
            lease,
            cancellationToken).ConfigureAwait(false);
        await EnsureDecisionAsync(
            boundary,
            ActionJournalStatus.RetentionApproved,
            cancellationToken).ConfigureAwait(false);

        while (boundary.State != RetentionLifecycleState.Completed)
        {
            switch (boundary.State)
            {
                case RetentionLifecycleState.Approved:
                    boundary = await _lifecycle.AdvanceAsync(
                        boundary,
                        boundary.Intent.Operation is null
                            ? RetentionLifecycleState.OperationDeleted
                            : RetentionLifecycleState.DeletingOperation,
                        lease,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case RetentionLifecycleState.DeletingOperation:
                    await _artifacts.VerifyLinkedStateAsync(
                        boundary.Intent.RehydrateLinkedState(),
                        CancellationToken.None).ConfigureAwait(false);
                    await RequireCurrentLeaseAsync(lease).ConfigureAwait(false);
                    _ = await _artifacts.DeleteOperationAsync(
                        boundary,
                        CancellationToken.None).ConfigureAwait(false);
                    _faultInjector?.AfterMutation(RetentionMutationKind.Operation);
                    boundary = await _lifecycle.AdvanceAsync(
                        boundary,
                        RetentionLifecycleState.OperationDeleted,
                        lease,
                        CancellationToken.None).ConfigureAwait(false);
                    break;

                case RetentionLifecycleState.OperationDeleted:
                    boundary = await _lifecycle.AdvanceAsync(
                        boundary,
                        boundary.Intent.Backup is null
                            ? RetentionLifecycleState.BackupDeleted
                            : RetentionLifecycleState.DeletingBackup,
                        lease,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case RetentionLifecycleState.DeletingBackup:
                    await _artifacts.VerifyLinkedStateAsync(
                        boundary.Intent.RehydrateLinkedState(),
                        CancellationToken.None).ConfigureAwait(false);
                    await RequireCurrentLeaseAsync(lease).ConfigureAwait(false);
                    _ = await _artifacts.DeleteBackupAsync(
                        boundary,
                        CancellationToken.None).ConfigureAwait(false);
                    _faultInjector?.AfterMutation(RetentionMutationKind.Backup);
                    boundary = await _lifecycle.AdvanceAsync(
                        boundary,
                        RetentionLifecycleState.BackupDeleted,
                        lease,
                        CancellationToken.None).ConfigureAwait(false);
                    break;

                case RetentionLifecycleState.BackupDeleted:
                    boundary = await _lifecycle.AdvanceAsync(
                        boundary,
                        boundary.Intent.ActionEvents.Count == 0
                            ? RetentionLifecycleState.ActionEventsDeleted
                            : RetentionLifecycleState.DeletingActionEvents,
                        lease,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case RetentionLifecycleState.DeletingActionEvents:
                    await _artifacts.VerifyLinkedStateAsync(
                        boundary.Intent.RehydrateLinkedState(),
                        CancellationToken.None).ConfigureAwait(false);
                    _ = await _artifacts.DeleteActionEventsAsync(
                        boundary,
                        lease,
                        CancellationToken.None).ConfigureAwait(false);
                    await _artifacts.RebuildActionIndexAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    _faultInjector?.AfterMutation(RetentionMutationKind.ActionEvents);
                    boundary = await _lifecycle.AdvanceAsync(
                        boundary,
                        RetentionLifecycleState.ActionEventsDeleted,
                        lease,
                        CancellationToken.None).ConfigureAwait(false);
                    break;

                case RetentionLifecycleState.ActionEventsDeleted:
                    await EnsureDecisionAsync(
                        boundary,
                        ActionJournalStatus.RetentionCompleted,
                        CancellationToken.None).ConfigureAwait(false);
                    boundary = await _lifecycle.AdvanceAsync(
                        boundary,
                        RetentionLifecycleState.Completed,
                        lease,
                        CancellationToken.None).ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidDataException(
                        $"Unsupported durable retention state {boundary.State}.");
            }
        }

        return Result(boundary, resumed);
    }

    private async ValueTask EnsureDecisionAsync(
        RetentionTransactionBoundary boundary,
        ActionJournalStatus status,
        CancellationToken cancellationToken)
    {
        var transactionId = boundary.Intent.TransactionId;
        var current = await _actionJournal.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (current.Any(entry =>
                entry.OperationId == transactionId &&
                entry.Kind == ActionJournalEventKind.RetentionDecision &&
                entry.Status == status &&
                entry.CorrelationId == transactionId))
        {
            return;
        }

        _ = await _actionJournal.AppendAsync(
            new ActionJournalEntryDraft(
                transactionId,
                "winora.retention",
                ActionJournalEventKind.RetentionDecision,
                ActionJournalCategory.Retention,
                status,
                ActionJournalRisk.Low,
                ActionJournalPrivilege.StandardUser,
                ActionJournalSupportStatus.Supported,
                transactionId,
                TargetCorrelationHash: null,
                AffectedItemCount:
                    (boundary.Intent.Operation is null ? 0 : 1) +
                    (boundary.Intent.Backup is null ? 0 : 1) +
                    boundary.Intent.ActionEvents.Count),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RequireCurrentLeaseAsync(
        IMutationLeaseHandle lease)
    {
        if (!await lease.RevalidateAsync(CancellationToken.None).ConfigureAwait(false))
        {
            throw new RetentionLeaseLostException();
        }
    }

    private RetentionMaintenanceResult Result(
        RetentionTransactionBoundary boundary,
        bool resumed) =>
        new(
            boundary.Intent.TransactionId,
            boundary.State,
            resumed,
            boundary.Intent.ActionEvents.Count);

    private DateTimeOffset GetUtcNow()
    {
        var now = _timeProvider.GetUtcNow();
        ActionJournalSchema.ValidateTimestamp(now, nameof(now));
        return now;
    }
}
