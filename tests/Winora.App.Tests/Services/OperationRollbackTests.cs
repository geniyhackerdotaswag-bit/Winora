using Winora.App.Services;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Core.Journal;
using Winora.Infrastructure.Recovery;
using Xunit;

namespace Winora.App.Tests.Services;

/// <summary>
/// The single path back from an applied change, used both by recovery and by the history screen.
/// </summary>
/// <remarks>
/// The failure paths are the interesting ones. A rollback that reports success without restoring
/// anything, or that gives a reason the user cannot act on, breaks the one promise the whole app is
/// built around — so each refusal has to be specific and each has to happen before anything is
/// touched.
/// </remarks>
public sealed class OperationRollbackTests
{
    [Fact]
    public async Task An_operation_with_no_verified_boundary_is_refused()
    {
        var attempt = await Build(new StubJournal(null)).RollBackAsync(Guid.NewGuid());

        Assert.False(attempt.Succeeded);
        Assert.Equal("Recovery_JournalUnreadable", attempt.FailureResourceKey);
    }

    [Fact]
    public async Task An_unreadable_journal_is_refused_rather_than_throwing()
    {
        var attempt = await Build(StubJournal.Failing()).RollBackAsync(Guid.NewGuid());

        Assert.False(attempt.Succeeded);
        Assert.Equal("Recovery_JournalUnreadable", attempt.FailureResourceKey);
    }

    /// <summary>
    /// An operation planned before the archive existed cannot be undone, and saying so is the only
    /// honest answer: a reconstructed plan would carry a different digest and the coordinator would
    /// refuse it as drift anyway.
    /// </summary>
    [Fact]
    public async Task An_operation_with_no_archived_plan_is_refused()
    {
        var plan = TestPlans.Sample();
        var attempt = await Build(new StubJournal(BoundaryFor(plan))).RollBackAsync(plan.PlanId);

        Assert.False(attempt.Succeeded);
        Assert.Equal("Recovery_PlanMissing", attempt.FailureResourceKey);
    }

    /// <summary>
    /// A plan with no backup binding cannot be undone safely, and the reason has to say that rather
    /// than fall through to something generic the user cannot act on.
    /// </summary>
    [Fact]
    public async Task An_operation_with_a_plan_but_no_backup_is_refused()
    {
        var plan = TestPlans.Sample();
        var archive = new StubArchive();
        archive.Add(plan);

        var attempt = await Build(new StubJournal(BoundaryFor(plan)), archive).RollBackAsync(plan.PlanId);

        Assert.False(attempt.Succeeded);
        Assert.Equal("Recovery_BackupMissing", attempt.FailureResourceKey);
    }

    /// <summary>Nothing may be read from the backup store before the refusals above have passed.</summary>
    [Fact]
    public async Task A_refused_rollback_never_touches_the_backup_store()
    {
        var plan = TestPlans.Sample();
        var backups = new StubBackups();

        await Build(new StubJournal(BoundaryFor(plan)), backups: backups).RollBackAsync(plan.PlanId);

        Assert.Equal(0, backups.Reads);
    }

    private static OperationRollback Build(
        StubJournal journal,
        IChangePlanArchive? archive = null,
        StubBackups? backups = null)
    {
        var confirmation = new ConfirmationAuthority();
        var store = backups ?? new StubBackups();

        return new OperationRollback(
            journal,
            archive ?? new StubArchive(),
            store,
            new StubCatalog(),
            new ChangeCoordinator(journal, store, new StubLease(), new StubClock(), confirmation),
            confirmation,
            new ActionJournalWriter(new SilentActionJournal()));
    }

    private static DurableOperationBoundary BoundaryFor(ChangePlan plan) =>
        DurableOperationBoundary.Create(
            plan.PlanId,
            DurableOperationFacts.From(plan),
            revision: 2,
            OperationState.Prepared,
            plan.Steps[0].StepId,
            []);

    private sealed class StubJournal : IDurableOperationJournal
    {
        private readonly DurableOperationBoundary? _boundary;
        private readonly bool _fails;

        public StubJournal(DurableOperationBoundary? boundary, bool fails = false)
        {
            _boundary = boundary;
            _fails = fails;
        }

        public static StubJournal Failing() => new(null, fails: true);

        public ValueTask<IReadOnlyList<DurableOperationBoundary>> ScanIncompleteAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<DurableOperationBoundary>>([]);

        public ValueTask<DurableOperationBoundary?> ReadVerifiedBoundaryAsync(
            Guid operationId,
            CancellationToken cancellationToken) =>
            _fails
                ? throw new IOException("The journal could not be read.")
                : ValueTask.FromResult(_boundary);

        public ValueTask<DurableTransitionResult> CompareAndAppendAsync(
            OperationTransition transition,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("No test here should reach a durable write.");
    }

    private sealed class StubArchive : IChangePlanArchive
    {
        private readonly Dictionary<Guid, ChangePlan> _plans = [];

        public void Add(ChangePlan plan) => _plans[plan.PlanId] = plan;

        public Task SaveAsync(ChangePlan plan, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ChangePlan?> TryLoadAsync(Guid planId, CancellationToken cancellationToken) =>
            Task.FromResult(_plans.GetValueOrDefault(planId));
    }

    /// <summary>Counts reads, so a test can prove a refusal happened before the store was consulted.</summary>
    private sealed class StubBackups : IBackupRepository
    {
        public int Reads { get; private set; }

        public ValueTask<BackupReceipt> ReadAndVerifyOperationBackupAsync(
            ChangePlan plan,
            string backupId,
            string backupDigest,
            CancellationToken cancellationToken)
        {
            Reads++;
            throw new NotSupportedException();
        }

        public ValueTask<BackupReceipt> ReadAndVerifyAsync(
            RollbackPlan plan,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<BackupReceipt> ReadAndVerifyRecoveryCheckpointAsync(
            RollbackPlan plan,
            string checkpointId,
            string checkpointDigest,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<BackupReceipt> CreateAndVerifyAsync(
            ChangePlan plan,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<BackupReceipt> CreateRecoveryCheckpointAsync(
            RollbackPlan plan,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubCatalog : IOperationCatalog
    {
        public bool TryResolve(string operationId, out IOperation? operation)
        {
            operation = null;
            return false;
        }

        public IOperation Resolve(string operationId) =>
            throw new NotSupportedException("No test here should resolve an operation.");
    }

    private sealed class StubLease : IMutationLease
    {
        public ValueTask<IMutationLeaseHandle?> TryAcquireAsync(
            Guid operationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("No test here should take the mutation lease.");

        public ValueTask<IMutationLeaseHandle?> TryAcquireRecoveryAsync(
            Guid incompleteOperationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("No test here should take the recovery lease.");
    }

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    }

    /// <remarks>
    /// Accepts entries and keeps none. The audit trail is not what these tests are about, and the
    /// writer is required to survive a journal that misbehaves anyway.
    /// </remarks>
    private sealed class SilentActionJournal : IActionJournal
    {
        public ValueTask<ActionJournalEntry> AppendAsync(
            ActionJournalEntryDraft draft,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Journalling must never decide the outcome of a rollback.");

        public ValueTask<IReadOnlyList<ActionJournalEntry>> ReadAllAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ActionJournalEntry>>([]);
    }
}
