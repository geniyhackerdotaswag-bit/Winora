using Winora.Core.Journal;
using Winora.Infrastructure.Journal;
using Xunit;

namespace Winora.Infrastructure.Tests.Journal;

public sealed class ActionJournalRetentionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Policy_keeps_recent_and_linked_failure_evidence_and_selects_only_eligible_history()
    {
        var linkedOperationId = Guid.NewGuid();
        var oldSuccessful = Entry(Guid.NewGuid(), ActionJournalStatus.Succeeded, Now.AddDays(-366));
        var oldLinkedFailure = Entry(linkedOperationId, ActionJournalStatus.Failed, Now.AddDays(-500));
        var oldUnlinkedFailure = Entry(Guid.NewGuid(), ActionJournalStatus.Failed, Now.AddDays(-500));
        var recent = Entry(Guid.NewGuid(), ActionJournalStatus.Succeeded, Now.AddDays(-2));

        var selected = ActionJournalRetentionPolicy.SelectExpiredEvents(
            [oldSuccessful, oldLinkedFailure, oldUnlinkedFailure, recent],
            new HashSet<Guid> { linkedOperationId },
            Now,
            TimeSpan.FromDays(365),
            maximumEventCount: 25_000);

        Assert.Equal(
            [oldUnlinkedFailure.EventId, oldSuccessful.EventId],
            selected.Select(item => item.EventId));
        Assert.DoesNotContain(selected, item => item.EventId == oldLinkedFailure.EventId);
        Assert.DoesNotContain(selected, item => item.EventId == recent.EventId);
    }

    [Fact]
    public void Policy_applies_count_cap_without_selecting_linked_recovery_evidence()
    {
        var linkedOperationId = Guid.NewGuid();
        var oldest = Entry(Guid.NewGuid(), ActionJournalStatus.Succeeded, Now.AddDays(-4));
        var linkedRecovery = Entry(
            linkedOperationId,
            ActionJournalStatus.RecoveryRequired,
            Now.AddDays(-3));
        var middle = Entry(Guid.NewGuid(), ActionJournalStatus.Succeeded, Now.AddDays(-2));
        var newest = Entry(Guid.NewGuid(), ActionJournalStatus.Succeeded, Now.AddDays(-1));

        var selected = ActionJournalRetentionPolicy.SelectExpiredEvents(
            [oldest, linkedRecovery, middle, newest],
            new HashSet<Guid> { linkedOperationId },
            Now,
            TimeSpan.FromDays(365),
            maximumEventCount: 3);

        Assert.Equal([oldest.EventId], selected.Select(item => item.EventId));
        Assert.DoesNotContain(selected, item => item.EventId == linkedRecovery.EventId);
    }

    [Fact]
    public void Policy_uses_an_exclusive_365_day_age_cutoff()
    {
        var olderThanCutoff = Entry(
            Guid.NewGuid(),
            ActionJournalStatus.Succeeded,
            Now.AddDays(-365).AddTicks(-1));
        var exactlyAtCutoff = Entry(
            Guid.NewGuid(),
            ActionJournalStatus.Succeeded,
            Now.AddDays(-365));
        var newerThanCutoff = Entry(
            Guid.NewGuid(),
            ActionJournalStatus.Succeeded,
            Now.AddDays(-365).AddTicks(1));

        var selected = ActionJournalRetentionPolicy.SelectExpiredEvents(
            [newerThanCutoff, exactlyAtCutoff, olderThanCutoff],
            new HashSet<Guid>(),
            Now,
            TimeSpan.FromDays(365),
            maximumEventCount: 25_000);

        Assert.Equal([olderThanCutoff.EventId], selected.Select(item => item.EventId));
    }

    [Fact]
    public void Policy_caps_unprotected_events_at_exactly_twenty_five_thousand()
    {
        var entries = Enumerable.Range(0, 25_001)
            .Select(index => Entry(
                Guid.NewGuid(),
                ActionJournalStatus.Succeeded,
                Now.AddTicks(index - 25_001)))
            .ToArray();

        var selected = ActionJournalRetentionPolicy.SelectExpiredEvents(
            entries,
            new HashSet<Guid>(),
            Now,
            TimeSpan.FromDays(365),
            maximumEventCount: 25_000);

        Assert.Equal([entries[0].EventId], selected.Select(item => item.EventId));
    }

    [Fact]
    public void Equal_timestamp_count_selection_is_identical_for_every_input_permutation()
    {
        var timestamp = Now.AddDays(-1);
        var lowest = Entry(Guid.NewGuid(), ActionJournalStatus.Succeeded, timestamp) with
        {
            EventId = "00000000000000000000000000000001",
        };
        var middle = Entry(Guid.NewGuid(), ActionJournalStatus.Succeeded, timestamp) with
        {
            EventId = "00000000000000000000000000000002",
        };
        var highest = Entry(Guid.NewGuid(), ActionJournalStatus.Succeeded, timestamp) with
        {
            EventId = "00000000000000000000000000000003",
        };
        ActionJournalEntry[][] permutations =
        [
            [lowest, middle, highest],
            [lowest, highest, middle],
            [middle, lowest, highest],
            [middle, highest, lowest],
            [highest, lowest, middle],
            [highest, middle, lowest],
        ];

        foreach (var permutation in permutations)
        {
            var selected = ActionJournalRetentionPolicy.SelectExpiredEvents(
                permutation,
                new HashSet<Guid>(),
                Now,
                TimeSpan.FromDays(365),
                maximumEventCount: 2);

            Assert.Equal([lowest.EventId], selected.Select(item => item.EventId));
        }
    }

    private static ActionJournalEntry Entry(
        Guid operationId,
        ActionJournalStatus status,
        DateTimeOffset timestamp) =>
        new(
            Guid.NewGuid().ToString("N"),
            timestamp,
            operationId,
            "windows.effects.transparency",
            ActionJournalEventKind.Operation,
            ActionJournalCategory.WindowsPersonalization,
            status,
            ActionJournalRisk.Low,
            ActionJournalPrivilege.StandardUser,
            ActionJournalSupportStatus.Supported,
            Guid.NewGuid(),
            TargetCorrelationHash: null,
            AffectedItemCount: null);

}
