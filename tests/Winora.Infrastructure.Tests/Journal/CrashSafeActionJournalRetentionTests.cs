using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Core.Journal;
using Winora.Infrastructure.Backups;
using Winora.Infrastructure.Journal;
using Winora.Infrastructure.Operations;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;
using Winora.Infrastructure.Tests.Operations;
using Xunit;
using TestContext = Winora.Infrastructure.Tests.Journal.JournalTestContext;

namespace Winora.Infrastructure.Tests.Journal;

public sealed class CrashSafeActionJournalRetentionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid CompletedOperationId =
        Guid.Parse("6a641392-5582-4f84-b274-f5e607d0742a");

    [Fact]
    public async Task Durable_exact_set_is_approved_before_delete_and_each_delete_revalidates_held_lease()
    {
        using var fixture = RetentionFixture.Create();
        var transactionId = Guid.NewGuid();
        var lease = RecordingRetentionLease.Active(transactionId, epoch: 7);
        var selection = RetentionSelectionFactory.Complete();
        fixture.Store.Selection = selection;
        fixture.Store.BeforeDelete = async boundary =>
        {
            Assert.Equal(transactionId, boundary.Intent.TransactionId);
            Assert.Equal(selection.Operation, boundary.Intent.Operation);
            Assert.Equal(selection.Backup, boundary.Intent.Backup);
            Assert.Equal(selection.ActionEvents, boundary.Intent.ActionEvents);
            var decisions = await fixture.ActionJournal.ReadAllAsync(
                TestContext.Current.CancellationToken);
            Assert.Contains(
                decisions,
                item =>
                    item.OperationId == transactionId &&
                    item.Status == ActionJournalStatus.RetentionApproved);
        };

        var result = await fixture.Coordinator.RunAsync(
            lease,
            RetentionRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(RetentionLifecycleState.Completed, result.State);
        Assert.Equal(4, lease.RevalidateCalls);
        Assert.Equal(
            [RetentionMutationKind.Operation, RetentionMutationKind.Backup, RetentionMutationKind.ActionEvents],
            fixture.Store.Mutations);
        var boundary = await fixture.Lifecycle.ReadAsync(
            transactionId,
            TestContext.Current.CancellationToken);
        Assert.Equal(RetentionLifecycleState.Completed, boundary.State);
        Assert.True(boundary.Revision >= 7);
    }

    [Fact]
    public async Task Crash_after_mutation_is_discovered_and_recovery_retries_absent_artifact_idempotently()
    {
        using var fixture = RetentionFixture.Create(
            new CrashOnceAfterMutation(RetentionMutationKind.Operation));
        var transactionId = Guid.NewGuid();
        var firstLease = RecordingRetentionLease.Active(transactionId, epoch: 11);
        fixture.Store.Selection = RetentionSelectionFactory.Complete();

        await Assert.ThrowsAsync<InjectedRetentionCrashException>(async () =>
            await fixture.Coordinator.RunAsync(
                firstLease,
                RetentionRequest(),
                TestContext.Current.CancellationToken));

        var incomplete = Assert.Single(await fixture.Lifecycle.ScanIncompleteAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(transactionId, incomplete.Intent.TransactionId);
        Assert.Equal(RetentionLifecycleState.DeletingOperation, incomplete.State);
        Assert.False(fixture.Store.OperationPresent);

        var recoveryLease = RecordingRetentionLease.Recovery(transactionId, epoch: 12);
        var recovered = await fixture.Coordinator.ResumeAsync(
            recoveryLease,
            TestContext.Current.CancellationToken);

        Assert.Equal(RetentionLifecycleState.Completed, recovered.State);
        Assert.Equal(2, fixture.Store.OperationDeleteAttempts);
        Assert.Equal(1, fixture.Store.OperationPhysicalDeletes);
        Assert.Empty(await fixture.Lifecycle.ScanIncompleteAsync(
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData((int)RetentionMutationKind.Operation, (int)RetentionLifecycleState.DeletingOperation)]
    [InlineData((int)RetentionMutationKind.Backup, (int)RetentionLifecycleState.DeletingBackup)]
    [InlineData((int)RetentionMutationKind.ActionEvents, (int)RetentionLifecycleState.DeletingActionEvents)]
    public async Task Every_mutation_boundary_recovers_after_delete_before_result_persistence(
        int crashAfterValue,
        int expectedIncompleteStateValue)
    {
        var crashAfter = (RetentionMutationKind)crashAfterValue;
        var expectedIncompleteState = (RetentionLifecycleState)expectedIncompleteStateValue;
        using var fixture = RetentionFixture.Create(new CrashOnceAfterMutation(crashAfter));
        var transactionId = Guid.NewGuid();
        fixture.Store.Selection = RetentionSelectionFactory.Complete();

        await Assert.ThrowsAsync<InjectedRetentionCrashException>(async () =>
            await fixture.Coordinator.RunAsync(
                RecordingRetentionLease.Active(transactionId, epoch: 20),
                RetentionRequest(),
                TestContext.Current.CancellationToken));

        var incomplete = Assert.Single(await fixture.Lifecycle.ScanIncompleteAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(expectedIncompleteState, incomplete.State);
        var recovered = await fixture.Coordinator.ResumeAsync(
            RecordingRetentionLease.Recovery(transactionId, epoch: 21),
            TestContext.Current.CancellationToken);

        Assert.Equal(RetentionLifecycleState.Completed, recovered.State);
        Assert.Equal(1, fixture.Store.OperationPhysicalDeletes);
        Assert.Equal(1, fixture.Store.BackupPhysicalDeletes);
        Assert.Equal(1, fixture.Store.ActionEventPhysicalDeletes);
    }

    [Fact]
    public async Task Lost_or_replaced_lease_fails_closed_immediately_before_mutation()
    {
        using var fixture = RetentionFixture.Create();
        var transactionId = Guid.NewGuid();
        fixture.Store.Selection = RetentionSelectionFactory.Complete();
        var lease = RecordingRetentionLease.Active(transactionId, epoch: 3);
        lease.RevalidationResults.Enqueue(true);
        lease.RevalidationResults.Enqueue(false);

        await Assert.ThrowsAsync<RetentionLeaseLostException>(async () =>
            await fixture.Coordinator.RunAsync(
                lease,
                RetentionRequest(),
                TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Store.Mutations);
        var incomplete = await fixture.Lifecycle.ReadAsync(
            transactionId,
            TestContext.Current.CancellationToken);
        Assert.Equal(RetentionLifecycleState.DeletingOperation, incomplete.State);
    }

    [Fact]
    public async Task Parallel_calls_for_same_transaction_do_not_run_parallel_deletes()
    {
        using var fixture = RetentionFixture.Create();
        var transactionId = Guid.NewGuid();
        fixture.Store.Selection = RetentionSelectionFactory.Complete();
        fixture.Store.DelayDeletes = true;
        var lease = RecordingRetentionLease.Active(transactionId, epoch: 5);

        var first = fixture.Coordinator.RunAsync(
            lease,
            RetentionRequest(),
            TestContext.Current.CancellationToken).AsTask();
        var second = fixture.Coordinator.RunAsync(
            lease,
            RetentionRequest(),
            TestContext.Current.CancellationToken).AsTask();
        await Task.WhenAll(first, second);

        Assert.Equal(1, fixture.Store.MaximumConcurrentDeletes);
        Assert.Equal(1, fixture.Store.OperationPhysicalDeletes);
        Assert.Equal(1, fixture.Store.BackupPhysicalDeletes);
        Assert.Equal(1, fixture.Store.ActionEventPhysicalDeletes);
    }

    [Fact]
    public async Task Different_lease_cannot_resume_without_higher_epoch_recovery_takeover()
    {
        using var fixture = RetentionFixture.Create(
            new CrashOnceAfterMutation(RetentionMutationKind.Operation));
        var transactionId = Guid.NewGuid();
        fixture.Store.Selection = RetentionSelectionFactory.Complete();
        await Assert.ThrowsAsync<InjectedRetentionCrashException>(async () =>
            await fixture.Coordinator.RunAsync(
                RecordingRetentionLease.Active(transactionId, epoch: 4),
                RetentionRequest(),
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Coordinator.ResumeAsync(
                RecordingRetentionLease.Active(transactionId, epoch: 5),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Coordinator.ResumeAsync(
                RecordingRetentionLease.Recovery(transactionId, epoch: 4),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, fixture.Store.OperationDeleteAttempts);
    }

    [Fact]
    public async Task Missing_transaction_has_no_authority_to_treat_absent_artifacts_as_deleted()
    {
        using var fixture = RetentionFixture.Create();
        var transactionId = Guid.NewGuid();

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await fixture.Coordinator.ResumeAsync(
                RecordingRetentionLease.Recovery(transactionId, epoch: 2),
                TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Store.Mutations);
    }

    [Fact]
    public async Task Production_orchestrator_acquires_and_holds_shared_mutation_lease_until_completion()
    {
        using var fixture = RetentionFixture.Create();
        fixture.Store.Selection = RetentionSelectionFactory.Complete();
        var transactionId = Guid.NewGuid();
        var lease = RecordingRetentionLease.Active(transactionId, epoch: 8);
        fixture.Store.IsLeaseDisposed = () => lease.Disposed;
        var provider = new RecordingMutationLease(lease);
        var service = new ActionJournalRetentionService(
            provider,
            fixture.Coordinator,
            () => transactionId);

        var result = await service.RunAsync(
            RetentionRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(transactionId, provider.AcquiredOperationId);
        Assert.Equal(RetentionLifecycleState.Completed, result.State);
        Assert.True(lease.Disposed);
        Assert.All(fixture.Store.MutationObservedLeaseDisposed, Assert.False);
    }

    [Fact]
    public async Task Production_orchestrator_holds_lease_through_error_then_disposes_it()
    {
        using var fixture = RetentionFixture.Create();
        fixture.Store.Selection = RetentionSelectionFactory.Complete();
        var transactionId = Guid.NewGuid();
        var lease = RecordingRetentionLease.Active(transactionId, epoch: 9);
        lease.RevalidationResults.Enqueue(true);
        lease.RevalidationResults.Enqueue(false);
        var service = new ActionJournalRetentionService(
            new RecordingMutationLease(lease),
            fixture.Coordinator,
            () => transactionId);

        await Assert.ThrowsAsync<RetentionLeaseLostException>(async () =>
            await service.RunAsync(
                RetentionRequest(),
                TestContext.Current.CancellationToken));

        Assert.True(lease.Disposed);
        Assert.Empty(fixture.Store.Mutations);
    }

    [Fact]
    public async Task Published_intent_recovers_as_approved_when_initial_state_publication_fails()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Winora.Tests",
            "RetentionIntentBoundary",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new WinoraDataPaths(root);
            var clock = new MutableJournalTimeProvider(Now);
            var documents = new AtomicJsonFile(
                paths,
                publisher: new FailSecondPublicationPublisher(),
                timeProvider: clock);
            var lifecycle = new DurableRetentionJournal(paths, documents, clock);
            var transactionId = Guid.NewGuid();

            await Assert.ThrowsAsync<InjectedProjectionStorageException>(async () =>
                await lifecycle.CreateApprovedAsync(
                    transactionId,
                    RecordingRetentionLease.Active(transactionId, epoch: 31),
                    RetentionRequest(),
                    RetentionSelectionFactory.Complete(),
                    TestContext.Current.CancellationToken));

            Assert.True(File.Exists(paths.GetRetentionIntentFile(transactionId.ToString("N"))));
            Assert.False(File.Exists(paths.GetRetentionStateFile(transactionId.ToString("N"))));

            var recovered = await new DurableRetentionJournal(paths, clock).ReadAsync(
                transactionId,
                TestContext.Current.CancellationToken);
            Assert.Equal(RetentionLifecycleState.Approved, recovered.State);
            Assert.Equal(0, recovered.Revision);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Resume_after_approved_decision_publication_does_not_duplicate_the_decision()
    {
        using var fixture = RetentionFixture.Create();
        var transactionId = Guid.NewGuid();
        var lease = RecordingRetentionLease.Active(transactionId, epoch: 32);
        var selection = RetentionSelectionFactory.Complete();
        fixture.Store.Selection = selection;
        var boundary = await fixture.Lifecycle.CreateApprovedAsync(
            transactionId,
            lease,
            RetentionRequest(),
            selection,
            TestContext.Current.CancellationToken);
        await AppendRetentionDecisionAsync(
            fixture.ActionJournal,
            boundary,
            ActionJournalStatus.RetentionApproved);

        var result = await fixture.Coordinator.ResumeAsync(
            lease,
            TestContext.Current.CancellationToken);

        Assert.Equal(RetentionLifecycleState.Completed, result.State);
        var decisions = await fixture.ActionJournal.ReadAllAsync(
            TestContext.Current.CancellationToken);
        Assert.Single(decisions, item =>
            item.OperationId == transactionId &&
            item.Status == ActionJournalStatus.RetentionApproved);
        Assert.Single(decisions, item =>
            item.OperationId == transactionId &&
            item.Status == ActionJournalStatus.RetentionCompleted);
    }

    [Fact]
    public async Task Resume_after_completed_decision_publication_only_advances_durable_state()
    {
        using var fixture = RetentionFixture.Create();
        var transactionId = Guid.NewGuid();
        var lease = RecordingRetentionLease.Active(transactionId, epoch: 33);
        var selection = RetentionSelectionFactory.Complete();
        fixture.Store.Selection = selection;
        var boundary = await fixture.Lifecycle.CreateApprovedAsync(
            transactionId,
            lease,
            RetentionRequest(),
            selection,
            TestContext.Current.CancellationToken);
        foreach (var state in new[]
                 {
                     RetentionLifecycleState.DeletingOperation,
                     RetentionLifecycleState.OperationDeleted,
                     RetentionLifecycleState.DeletingBackup,
                     RetentionLifecycleState.BackupDeleted,
                     RetentionLifecycleState.DeletingActionEvents,
                     RetentionLifecycleState.ActionEventsDeleted,
                 })
        {
            boundary = await fixture.Lifecycle.AdvanceAsync(
                boundary,
                state,
                lease,
                TestContext.Current.CancellationToken);
        }

        await AppendRetentionDecisionAsync(
            fixture.ActionJournal,
            boundary,
            ActionJournalStatus.RetentionApproved);
        await AppendRetentionDecisionAsync(
            fixture.ActionJournal,
            boundary,
            ActionJournalStatus.RetentionCompleted);

        var result = await fixture.Coordinator.ResumeAsync(
            lease,
            TestContext.Current.CancellationToken);

        Assert.Equal(RetentionLifecycleState.Completed, result.State);
        Assert.Empty(fixture.Store.Mutations);
        var decisions = await fixture.ActionJournal.ReadAllAsync(
            TestContext.Current.CancellationToken);
        Assert.Single(decisions, item =>
            item.OperationId == transactionId &&
            item.Status == ActionJournalStatus.RetentionApproved);
        Assert.Single(decisions, item =>
            item.OperationId == transactionId &&
            item.Status == ActionJournalStatus.RetentionCompleted);
    }

    private static async ValueTask AppendRetentionDecisionAsync(
        IActionJournal journal,
        RetentionTransactionBoundary boundary,
        ActionJournalStatus status)
    {
        _ = await journal.AppendAsync(
            new ActionJournalEntryDraft(
                boundary.Intent.TransactionId,
                "winora.retention",
                ActionJournalEventKind.RetentionDecision,
                ActionJournalCategory.Retention,
                status,
                ActionJournalRisk.Low,
                ActionJournalPrivilege.StandardUser,
                ActionJournalSupportStatus.Supported,
                boundary.Intent.TransactionId,
                TargetCorrelationHash: null,
                AffectedItemCount:
                    (boundary.Intent.Operation is null ? 0 : 1) +
                    (boundary.Intent.Backup is null ? 0 : 1) +
                    boundary.Intent.ActionEvents.Count),
            TestContext.Current.CancellationToken);
    }

    private static ActionJournalRetentionRequest RetentionRequest() =>
        new(
            CompletedOperationId,
            new HashSet<Guid>(),
            TimeSpan.FromDays(365),
            25_000);

    private sealed class RetentionFixture : IDisposable
    {
        private RetentionFixture(
            string root,
            ActionJournal journal,
            DurableRetentionJournal lifecycle,
            RecordingRetentionArtifactStore store,
            ActionJournalRetentionCoordinator coordinator)
        {
            Root = root;
            ActionJournal = journal;
            Lifecycle = lifecycle;
            Store = store;
            Coordinator = coordinator;
        }

        internal string Root { get; }

        internal ActionJournal ActionJournal { get; }

        internal DurableRetentionJournal Lifecycle { get; }

        internal RecordingRetentionArtifactStore Store { get; }

        internal ActionJournalRetentionCoordinator Coordinator { get; }

        internal static RetentionFixture Create(
            IRetentionMaintenanceFaultInjector? faultInjector = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "Winora.Tests",
                "CrashSafeRetention",
                Guid.NewGuid().ToString("N"));
            var paths = new WinoraDataPaths(root);
            var clock = new MutableJournalTimeProvider(Now);
            var journal = new ActionJournal(
                paths,
                new FixedActionJournalOperationCatalog(
                    ["windows.effects.transparency", "winora.retention"]),
                clock);
            var lifecycle = new DurableRetentionJournal(paths, clock);
            var store = new RecordingRetentionArtifactStore();
            return new RetentionFixture(
                root,
                journal,
                lifecycle,
                store,
                new ActionJournalRetentionCoordinator(
                    journal,
                    lifecycle,
                    store,
                    clock,
                    faultInjector));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private static class RetentionSelectionFactory
    {
        internal static RetentionArtifactSelection Complete()
        {
            var backupId = Guid.NewGuid().ToString("N");
            var operation = new RetentionOperationIdentity(
                CompletedOperationId,
                Revision: 9,
                OperationState.Completed,
                LastEventHash: new string('A', 64),
                TerminalOccurredAtUtc: Now.AddDays(-100),
                PlanDigest: new string('B', 64),
                backupId,
                BackupDigest: new string('C', 64),
                RootVolumeSerialNumber: 1,
                RootFileIndex: 1,
                IsTerminal: true,
                IsRecoveryProtected: false);
            var backup = new RetentionBackupIdentity(
                backupId,
                BackupStorageStatus.VerifiedCommitted,
                BackupCaptureKind.Operation,
                operation.PlanDigest,
                operation.BackupDigest!,
                BackupProtectionClass.OperationRollbackSource,
                IsVerified: true,
                IsRecoveryProtected: true,
                CommittedUtc: Now.AddDays(-100));
            var actionEvent = new RetentionActionEventIdentity(
                Guid.NewGuid().ToString("N"),
                PayloadSha256: new string('D', 64),
                VolumeSerialNumber: 7,
                FileIndex: 11);
            var linkedState = RetentionLinkedStateSnapshot.Create(
                [
                    new OperationStorageCatalogEntry(
                        operation.OperationId,
                        operation.Revision,
                        operation.State,
                        operation.LastEventHash,
                        operation.TerminalOccurredAtUtc,
                        operation.PlanDigest,
                        operation.BackupId,
                        operation.BackupDigest,
                        operation.RootVolumeSerialNumber,
                        operation.RootFileIndex,
                        operation.IsTerminal,
                        operation.IsRecoveryProtected),
                ],
                operation.OperationId);
            return new RetentionArtifactSelection(
                operation,
                backup,
                [actionEvent],
                linkedState);
        }
    }

    private sealed class RecordingRetentionLease : IMutationLeaseHandle
    {
        private RecordingRetentionLease(
            Guid operationId,
            long epoch,
            bool recovery)
        {
            OperationId = operationId;
            Epoch = epoch;
            IsRecoveryTakeover = recovery;
            LeaseId = Guid.NewGuid();
        }

        internal Queue<bool> RevalidationResults { get; } = new();

        internal int RevalidateCalls { get; private set; }

        internal bool Disposed { get; private set; }

        public Guid LeaseId { get; }

        public Guid OperationId { get; }

        public long Epoch { get; }

        public bool IsRecoveryTakeover { get; }

        internal static RecordingRetentionLease Active(Guid operationId, long epoch) =>
            new(operationId, epoch, recovery: false);

        internal static RecordingRetentionLease Recovery(Guid operationId, long epoch) =>
            new(operationId, epoch, recovery: true);

        public ValueTask<bool> RevalidateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevalidateCalls++;
            return ValueTask.FromResult(
                RevalidationResults.Count == 0 || RevalidationResults.Dequeue());
        }

        public ValueTask<bool> HeartbeatAsync(CancellationToken cancellationToken) =>
            RevalidateAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingRetentionArtifactStore : IRetentionArtifactStore
    {
        private int _concurrentDeletes;

        internal RetentionArtifactSelection Selection { get; set; } =
            RetentionArtifactSelection.Empty;

        internal Func<RetentionTransactionBoundary, Task>? BeforeDelete { get; set; }

        internal bool DelayDeletes { get; set; }

        internal bool OperationPresent { get; private set; } = true;

        internal bool BackupPresent { get; private set; } = true;

        internal bool ActionEventsPresent { get; private set; } = true;

        internal int OperationDeleteAttempts { get; private set; }

        internal int OperationPhysicalDeletes { get; private set; }

        internal int BackupPhysicalDeletes { get; private set; }

        internal int ActionEventPhysicalDeletes { get; private set; }

        internal int MaximumConcurrentDeletes { get; private set; }

        internal List<RetentionMutationKind> Mutations { get; } = [];

        internal List<bool> MutationObservedLeaseDisposed { get; } = [];

        internal Func<bool>? IsLeaseDisposed { get; set; }

        public ValueTask<RetentionArtifactSelection> CaptureAsync(
            ActionJournalRetentionRequest request,
            DateTimeOffset nowUtc,
            int reservedDecisionEventCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Selection);
        }

        public ValueTask VerifyLinkedStateAsync(
            RetentionLinkedStateSnapshot expected,
            CancellationToken cancellationToken)
        {
            expected.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            var captured = Selection.LinkedState;
            if (expected.SchemaVersion != captured.SchemaVersion ||
                expected.ExcludedCompletedOperationId != captured.ExcludedCompletedOperationId ||
                !StringComparer.Ordinal.Equals(expected.CatalogSha256, captured.CatalogSha256) ||
                !expected.LinkedOperationIds.SequenceEqual(captured.LinkedOperationIds) ||
                !StringComparer.Ordinal.Equals(expected.SnapshotSha256, captured.SnapshotSha256))
            {
                throw new InvalidDataException(
                    "The test retention linked-state snapshot changed after capture.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteOperationAsync(
            RetentionTransactionBoundary boundary,
            CancellationToken cancellationToken) =>
            DeleteAsync(
                boundary,
                RetentionMutationKind.Operation,
                () =>
                {
                    OperationDeleteAttempts++;
                    if (!OperationPresent)
                    {
                        return false;
                    }

                    OperationPresent = false;
                    OperationPhysicalDeletes++;
                    return true;
                },
                cancellationToken);

        public ValueTask<bool> DeleteBackupAsync(
            RetentionTransactionBoundary boundary,
            CancellationToken cancellationToken) =>
            DeleteAsync(
                boundary,
                RetentionMutationKind.Backup,
                () =>
                {
                    if (!BackupPresent)
                    {
                        return false;
                    }

                    BackupPresent = false;
                    BackupPhysicalDeletes++;
                    return true;
                },
                cancellationToken);

        public async ValueTask<int> DeleteActionEventsAsync(
            RetentionTransactionBoundary boundary,
            IMutationLeaseHandle lease,
            CancellationToken cancellationToken)
        {
            if (!await lease.RevalidateAsync(CancellationToken.None))
            {
                throw new RetentionLeaseLostException();
            }

            var changed = await DeleteAsync(
                boundary,
                RetentionMutationKind.ActionEvents,
                () =>
                {
                    if (!ActionEventsPresent)
                    {
                        return false;
                    }

                    ActionEventsPresent = false;
                    ActionEventPhysicalDeletes++;
                    return true;
                },
                cancellationToken);
            return changed ? boundary.Intent.ActionEvents.Count : 0;
        }

        public ValueTask RebuildActionIndexAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        private async ValueTask<bool> DeleteAsync(
            RetentionTransactionBoundary boundary,
            RetentionMutationKind kind,
            Func<bool> delete,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var concurrent = Interlocked.Increment(ref _concurrentDeletes);
            MaximumConcurrentDeletes = Math.Max(MaximumConcurrentDeletes, concurrent);
            try
            {
                if (BeforeDelete is not null)
                {
                    await BeforeDelete(boundary);
                }

                if (DelayDeletes)
                {
                    await Task.Yield();
                }

                Mutations.Add(kind);
                MutationObservedLeaseDisposed.Add(IsLeaseDisposed?.Invoke() ?? false);
                return delete();
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentDeletes);
            }
        }
    }

    private sealed class CrashOnceAfterMutation(RetentionMutationKind target) :
        IRetentionMaintenanceFaultInjector
    {
        private int _crashed;

        public void AfterMutation(RetentionMutationKind kind)
        {
            if (kind == target && Interlocked.Exchange(ref _crashed, 1) == 0)
            {
                throw new InjectedRetentionCrashException();
            }
        }
    }

    private sealed class InjectedRetentionCrashException : Exception;

    private sealed class RecordingMutationLease(
        RecordingRetentionLease handle) : IMutationLease
    {
        internal Guid? AcquiredOperationId { get; private set; }

        public ValueTask<IMutationLeaseHandle?> TryAcquireAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(handle.OperationId, operationId);
            AcquiredOperationId = operationId;
            return ValueTask.FromResult<IMutationLeaseHandle?>(handle);
        }

        public ValueTask<IMutationLeaseHandle?> TryAcquireRecoveryAsync(
            Guid incompleteOperationId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IMutationLeaseHandle?>(null);
    }
}
