using Winora.App.Services;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Xunit;

namespace Winora.App.Tests.Services;

/// <summary>
/// The way out of a state where every further change is refused.
/// </summary>
/// <remarks>
/// The mutation lease will not be granted while any operation is incomplete, so one operation left
/// unfinished blocks the whole app until it is reconciled. What is tested here is the part
/// <see cref="RecoveryState" /> owns: finding the incomplete operations and reporting honestly on a
/// batch of them. Undoing a single operation belongs to <see cref="OperationRollback" /> and is
/// covered by <see cref="OperationRollbackTests" />.
/// </remarks>
public sealed class RecoveryStateTests
{
    [Fact]
    public async Task Pending_count_reports_what_the_journal_found()
    {
        var state = Build(StubJournal.With(Boundary(), Boundary()));

        Assert.Equal(2, await state.PendingCountAsync());
    }

    [Fact]
    public async Task Pending_count_is_zero_when_nothing_is_incomplete()
    {
        Assert.Equal(0, await Build(StubJournal.With()).PendingCountAsync());
    }

    /// <summary>
    /// An unreadable journal must not be reported as a pending recovery. Claiming work is pending
    /// when that is merely unknown would send the user to a screen that cannot help them.
    /// </summary>
    [Fact]
    public async Task Pending_count_is_zero_when_the_journal_cannot_be_read()
    {
        Assert.Equal(0, await Build(StubJournal.Failing()).PendingCountAsync());
    }

    [Fact]
    public async Task Recovery_reports_an_unreadable_journal_as_a_failure_with_a_reason()
    {
        var outcome = await Build(StubJournal.Failing()).RecoverAsync();

        Assert.Equal(0, outcome.Recovered);
        Assert.Equal(1, outcome.Failed);
        Assert.Equal("Recovery_JournalUnreadable", outcome.FirstFailure);
    }

    [Fact]
    public async Task Nothing_incomplete_means_nothing_recovered_and_nothing_failed()
    {
        var outcome = await Build(StubJournal.With()).RecoverAsync();

        Assert.Equal(0, outcome.Recovered);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(string.Empty, outcome.FirstFailure);
    }

    [Fact]
    public async Task Every_incomplete_operation_is_rolled_back()
    {
        var rollback = new StubRollback();
        var outcome = await Build(StubJournal.With(Boundary(), Boundary(), Boundary()), rollback)
            .RecoverAsync();

        Assert.Equal(3, outcome.Recovered);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(3, rollback.Attempts.Count);
    }

    /// <summary>One operation that cannot be undone must not stop the others from being attempted.</summary>
    [Fact]
    public async Task A_failure_does_not_stop_the_remaining_operations()
    {
        var rollback = new StubRollback { FailEveryOther = true };

        var outcome = await Build(StubJournal.With(Boundary(), Boundary(), Boundary()), rollback)
            .RecoverAsync();

        Assert.Equal(3, rollback.Attempts.Count);
        Assert.Equal(2, outcome.Failed);
        Assert.Equal(1, outcome.Recovered);
    }

    /// <summary>
    /// The reported reason is the first one. A later failure overwriting it would hide the only
    /// message that explains what actually went wrong.
    /// </summary>
    [Fact]
    public async Task The_reported_reason_is_the_first_failure_not_the_last()
    {
        var rollback = new StubRollback { Reasons = ["Recovery_PlanMissing", "Recovery_BackupMissing"] };

        var outcome = await Build(StubJournal.With(Boundary(), Boundary()), rollback).RecoverAsync();

        Assert.Equal(2, outcome.Failed);
        Assert.Equal("Recovery_PlanMissing", outcome.FirstFailure);
    }

    [Fact]
    public async Task Cancellation_is_observed_rather_than_swallowed()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Build(StubJournal.With(Boundary())).RecoverAsync(cancellation.Token));
    }

    private static RecoveryState Build(StubJournal journal, StubRollback? rollback = null) =>
        new(journal, rollback ?? new StubRollback(), new EchoLocalization());

    /// <summary>An operation left mid-flight: the state that blocks every other change.</summary>
    private static DurableOperationBoundary Boundary()
    {
        var plan = TestPlans.Sample();

        return DurableOperationBoundary.Create(
            plan.PlanId,
            DurableOperationFacts.From(plan),
            revision: 2,
            OperationState.Prepared,
            plan.Steps[0].StepId,
            []);
    }

    /// <summary>Returns the key itself, so a test can assert which message was chosen.</summary>
    private sealed class EchoLocalization : ILocalizationService
    {
        public string Get(string resourceKey) => resourceKey;

        public bool IsAvailable => true;
    }

    private sealed class StubRollback : IOperationRollback
    {
        public List<Guid> Attempts { get; } = [];

        public bool FailEveryOther { get; init; }

        /// <summary>Failure reasons handed out in order, when the stub is set to fail.</summary>
        public IReadOnlyList<string> Reasons { get; init; } = [];

        public Task<RollbackAttempt> RollBackAsync(Guid operationId, CancellationToken cancellationToken)
        {
            Attempts.Add(operationId);

            if (Reasons.Count > 0)
            {
                var reason = Reasons[Math.Min(Attempts.Count - 1, Reasons.Count - 1)];
                return Task.FromResult(new RollbackAttempt(false, reason));
            }

            var fails = FailEveryOther && Attempts.Count % 2 == 1;
            return Task.FromResult(fails
                ? new RollbackAttempt(false, "Recovery_Failed")
                : new RollbackAttempt(true, string.Empty));
        }
    }

    private sealed class StubJournal : IDurableOperationJournal
    {
        private readonly IReadOnlyList<DurableOperationBoundary> _incomplete;
        private readonly bool _fails;

        private StubJournal(IReadOnlyList<DurableOperationBoundary> incomplete, bool fails)
        {
            _incomplete = incomplete;
            _fails = fails;
        }

        public static StubJournal With(params DurableOperationBoundary[] incomplete) =>
            new(incomplete, fails: false);

        public static StubJournal Failing() => new([], fails: true);

        public ValueTask<IReadOnlyList<DurableOperationBoundary>> ScanIncompleteAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _fails
                ? throw new IOException("The journal could not be read.")
                : ValueTask.FromResult(_incomplete);
        }

        public ValueTask<DurableOperationBoundary?> ReadVerifiedBoundaryAsync(
            Guid operationId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                _incomplete.FirstOrDefault(boundary => boundary.OperationId == operationId));

        public ValueTask<DurableTransitionResult> CompareAndAppendAsync(
            OperationTransition transition,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("No test here should reach a durable write.");
    }
}
