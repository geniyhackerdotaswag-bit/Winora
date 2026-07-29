using System.Diagnostics;
using System.Text.Json.Nodes;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Infrastructure.Operations;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;
using Winora.Infrastructure.ProcessHost;
using Winora.Infrastructure.Tests.Persistence;
using Xunit;

namespace Winora.Infrastructure.Tests.Operations;

public sealed class DurableOperationJournalTests
{
    private static readonly DateTimeOffset OccurredUtc =
        new(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Immutable_transition_is_durable_and_rebuilds_the_boundary()
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);

        var result = await fixture.Journal.CompareAndAppendAsync(
            fixture.Planned(),
            CancellationToken.None);
        var boundary = await fixture.Journal.ReadVerifiedBoundaryAsync(
            fixture.Plan.PlanId,
            CancellationToken.None);

        Assert.True(result.IsDurable);
        Assert.Equal(1, result.Revision);
        Assert.NotNull(boundary);
        Assert.Equal(OperationState.Planned, boundary.State);
        Assert.Equal(fixture.Plan.Digest, boundary.Facts.PlanDigest);
        Assert.Equal(fixture.Plan.OperationId, boundary.Facts.CatalogOperationId);
        Assert.Equal(
            fixture.Plan.Steps.Select(step => step.StepId),
            boundary.Facts.RecoverySteps.Select(descriptor => descriptor.StepId));
        Assert.Equal(
            fixture.Plan.Steps.Select(step => step.SourceFingerprint),
            boundary.Facts.RecoverySteps.Select(descriptor => descriptor.SourceFingerprint));
        Assert.Equal(
            fixture.Plan.Steps.Select(step => step.ResultFingerprint),
            boundary.Facts.RecoverySteps.Select(descriptor => descriptor.ResultFingerprint));
        Assert.Empty(boundary.AppliedStepIds);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(fixture.Paths.GetOperationDirectory(fixture.OperationKey), "Transitions"),
            "1-*.json"));
    }

    [Fact]
    public async Task Competing_expected_revision_allows_exactly_one_new_transition()
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        var first = fixture.Planned(OccurredUtc);
        var second = fixture.Planned(OccurredUtc.AddTicks(1));

        var results = await Task.WhenAll(
            fixture.Journal.CompareAndAppendAsync(first, CancellationToken.None).AsTask(),
            fixture.SecondJournal.CompareAndAppendAsync(second, CancellationToken.None).AsTask());

        Assert.Single(results, result => result.IsDurable);
        Assert.Single(results, result => !result.IsDurable);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(fixture.Paths.GetOperationDirectory(fixture.OperationKey), "Transitions"),
            "1-*.json"));
    }

    [Fact]
    public async Task Missing_projection_is_rebuilt_from_immutable_events()
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        await fixture.AppendAsync(fixture.Planned());
        File.Delete(fixture.Paths.GetOperationManifestFile(fixture.OperationKey));

        var boundary = await fixture.SecondJournal.ReadVerifiedBoundaryAsync(
            fixture.Plan.PlanId,
            CancellationToken.None);

        Assert.NotNull(boundary);
        Assert.Equal(OperationState.Planned, boundary.State);
        Assert.True(File.Exists(fixture.Paths.GetOperationManifestFile(fixture.OperationKey)));
    }

    [Fact]
    public async Task Corrupt_projection_is_ignored_and_rebuilt_from_immutable_events()
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        await fixture.AppendAsync(fixture.Planned());
        await File.WriteAllTextAsync(
            fixture.Paths.GetOperationManifestFile(fixture.OperationKey),
            "{corrupt");

        var boundary = await fixture.SecondJournal.ReadVerifiedBoundaryAsync(
            fixture.Plan.PlanId,
            CancellationToken.None);

        Assert.NotNull(boundary);
        Assert.Equal(OperationState.Planned, boundary.State);
    }

    [Fact]
    public async Task Corrupt_authoritative_transition_is_rejected_even_when_projection_is_valid()
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        await fixture.AppendAsync(fixture.Planned());
        var transitionPath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(fixture.Paths.GetOperationDirectory(fixture.OperationKey), "Transitions"),
            "*.json"));
        var json = await File.ReadAllTextAsync(transitionPath);
        await File.WriteAllTextAsync(
            transitionPath,
            json.Replace("\"state\":0", "\"state\":1", StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.SecondJournal.ReadVerifiedBoundaryAsync(
                fixture.Plan.PlanId,
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Tampered_hash_bound_transition_timestamp_is_rejected_by_storage_catalog_scan()
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        await fixture.AppendAsync(fixture.Planned());
        var transitionPath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(fixture.Paths.GetOperationDirectory(fixture.OperationKey), "Transitions"),
            "*.json"));
        var document = JsonNode.Parse(await File.ReadAllTextAsync(transitionPath)) ??
            throw new InvalidDataException("The transition test fixture is empty.");
        document["payload"]!["occurredAtUtc"] = OccurredUtc.AddDays(-100).ToString("O");
        await File.WriteAllTextAsync(transitionPath, document.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.SecondJournal.ScanStorageCatalogAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Rebuild_identifies_exact_uncertain_step_after_a_prior_verified_step()
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        var facts = DurableOperationFacts.From(fixture.Plan);
        var backedUpFacts = facts.WithBackupBinding("backup", "BACKUP-DIGEST");

        await fixture.AppendAsync(fixture.Planned());
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            facts,
            1,
            OperationState.Planned,
            OperationState.Prepared,
            null,
            OccurredUtc.AddMinutes(1),
            previousFacts: facts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            2,
            OperationState.Prepared,
            OperationState.BackupCreated,
            null,
            OccurredUtc.AddMinutes(2),
            previousFacts: facts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            3,
            OperationState.BackupCreated,
            OperationState.Applying,
            "step-1",
            OccurredUtc.AddMinutes(3),
            previousFacts: backedUpFacts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            4,
            OperationState.Applying,
            OperationState.Applied,
            "step-1",
            OccurredUtc.AddMinutes(4),
            previousFacts: backedUpFacts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            5,
            OperationState.Applied,
            OperationState.Verified,
            "step-1",
            OccurredUtc.AddMinutes(5),
            previousFacts: backedUpFacts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            6,
            OperationState.Verified,
            OperationState.Applying,
            "step-2",
            OccurredUtc.AddMinutes(6),
            previousFacts: backedUpFacts));

        var boundary = await fixture.SecondJournal.ReadVerifiedBoundaryAsync(
            fixture.Plan.PlanId,
            CancellationToken.None);

        Assert.NotNull(boundary);
        Assert.Equal(7, boundary.Revision);
        Assert.Equal(OperationState.Applying, boundary.State);
        Assert.Equal("step-2", boundary.StepId);
        Assert.Equal(["step-1"], boundary.AppliedStepIds);
    }

    [Fact]
    public async Task Append_rejects_apply_failed_no_changes_after_a_verified_step_prefix()
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        var facts = DurableOperationFacts.From(fixture.Plan);
        var backedUpFacts = facts.WithBackupBinding("backup", "BACKUP-DIGEST");

        await fixture.AppendAsync(fixture.Planned());
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId, facts, 1, OperationState.Planned, OperationState.Prepared,
            null, OccurredUtc.AddMinutes(1), previousFacts: facts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId, backedUpFacts, 2, OperationState.Prepared, OperationState.BackupCreated,
            null, OccurredUtc.AddMinutes(2), previousFacts: facts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId, backedUpFacts, 3, OperationState.BackupCreated, OperationState.Applying,
            "step-1", OccurredUtc.AddMinutes(3), previousFacts: backedUpFacts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId, backedUpFacts, 4, OperationState.Applying, OperationState.Applied,
            "step-1", OccurredUtc.AddMinutes(4), previousFacts: backedUpFacts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId, backedUpFacts, 5, OperationState.Applied, OperationState.Verified,
            "step-1", OccurredUtc.AddMinutes(5), previousFacts: backedUpFacts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId, backedUpFacts, 6, OperationState.Verified, OperationState.Applying,
            "step-2", OccurredUtc.AddMinutes(6), previousFacts: backedUpFacts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId, backedUpFacts, 7, OperationState.Applying, OperationState.ApplyStepNotApplied,
            "step-2", OccurredUtc.AddMinutes(7), previousFacts: backedUpFacts));

        var invalidTerminal = OperationTransition.Create(
            fixture.Plan.PlanId, backedUpFacts, 8,
            OperationState.ApplyStepNotApplied, OperationState.ApplyFailedNoChanges,
            "step-2", OccurredUtc.AddMinutes(8), previousFacts: backedUpFacts);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Journal.CompareAndAppendAsync(invalidTerminal, CancellationToken.None).AsTask());

        var incomplete = Assert.Single(await fixture.Journal.ScanIncompleteAsync(
            CancellationToken.None));
        Assert.Equal(OperationState.ApplyStepNotApplied, incomplete.State);
        Assert.Equal(["step-1"], incomplete.AppliedStepIds);
    }

    [Fact]
    public async Task Rebuild_rejects_applying_and_applied_events_for_different_steps_and_keeps_recovery_visible()
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        var facts = DurableOperationFacts.From(fixture.Plan);
        var backedUpFacts = facts.WithBackupBinding("backup", "BACKUP-DIGEST");

        await fixture.AppendAsync(fixture.Planned());
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            facts,
            1,
            OperationState.Planned,
            OperationState.Prepared,
            null,
            OccurredUtc.AddMinutes(1),
            previousFacts: facts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            2,
            OperationState.Prepared,
            OperationState.BackupCreated,
            null,
            OccurredUtc.AddMinutes(2),
            previousFacts: facts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            3,
            OperationState.BackupCreated,
            OperationState.Applying,
            "step-1",
            OccurredUtc.AddMinutes(3),
            previousFacts: backedUpFacts));

        var malicious = OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            4,
            OperationState.Applying,
            OperationState.Applied,
            "step-2",
            OccurredUtc.AddMinutes(4),
            previousFacts: backedUpFacts);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Journal.CompareAndAppendAsync(malicious, CancellationToken.None).AsTask());

        var applying = Assert.Single(await fixture.Journal.ScanIncompleteAsync(
            CancellationToken.None));
        Assert.Equal(OperationState.Applying, applying.State);
        Assert.Equal("step-1", applying.StepId);
        Assert.Empty(applying.AppliedStepIds);

        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            4,
            OperationState.Applying,
            OperationState.Applied,
            "step-1",
            OccurredUtc.AddMinutes(5),
            previousFacts: backedUpFacts));
        var mismatchedVerification = OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            5,
            OperationState.Applied,
            OperationState.Verified,
            "step-2",
            OccurredUtc.AddMinutes(6),
            previousFacts: backedUpFacts);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Journal.CompareAndAppendAsync(
                mismatchedVerification,
                CancellationToken.None).AsTask());
        var applied = Assert.Single(await fixture.Journal.ScanIncompleteAsync(
            CancellationToken.None));
        Assert.Equal(OperationState.Applied, applied.State);
        Assert.Equal("step-1", applied.StepId);
        Assert.Empty(applied.AppliedStepIds);
    }

    [Fact]
    public async Task Rebuild_rejects_completed_before_every_ordered_step_is_verified()
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        var facts = DurableOperationFacts.From(fixture.Plan);
        var backedUpFacts = facts.WithBackupBinding("backup", "BACKUP-DIGEST");

        await fixture.AppendAsync(fixture.Planned());
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId, facts, 1, OperationState.Planned, OperationState.Prepared,
            null, OccurredUtc.AddMinutes(1), previousFacts: facts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId, backedUpFacts, 2, OperationState.Prepared, OperationState.BackupCreated,
            null, OccurredUtc.AddMinutes(2), previousFacts: facts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId, backedUpFacts, 3, OperationState.BackupCreated, OperationState.Applying,
            "step-1", OccurredUtc.AddMinutes(3), previousFacts: backedUpFacts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId, backedUpFacts, 4, OperationState.Applying, OperationState.Applied,
            "step-1", OccurredUtc.AddMinutes(4), previousFacts: backedUpFacts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId, backedUpFacts, 5, OperationState.Applied, OperationState.Verified,
            "step-1", OccurredUtc.AddMinutes(5), previousFacts: backedUpFacts));

        var premature = OperationTransition.Create(
            fixture.Plan.PlanId, backedUpFacts, 6, OperationState.Verified, OperationState.Completed,
            null, OccurredUtc.AddMinutes(6), previousFacts: backedUpFacts);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Journal.CompareAndAppendAsync(premature, CancellationToken.None).AsTask());

        var incomplete = Assert.Single(await fixture.Journal.ScanIncompleteAsync(
            CancellationToken.None));
        Assert.Equal(OperationState.Verified, incomplete.State);
        Assert.Equal(["step-1"], incomplete.AppliedStepIds);
    }

    [Fact]
    public async Task Rebuild_rejects_rolled_back_before_reverse_ordered_steps_are_verified()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var journal = new DurableOperationJournal(paths, DurableJournalActor.App);
        var plan = TestPlan.Create();
        var rollback = RollbackPlan.Create(
            Guid.Parse("7e962729-7ced-449b-b475-ae8ff56ff393"),
            plan,
            BackupReceipt.Verified(
                plan.PlanId.ToString("N"),
                "BACKUP-DIGEST",
                plan.Digest,
                plan.SourceFingerprint,
                plan.SourceFingerprint),
            TestPlan.Fingerprint("aggregate-applied"));
        var facts = DurableOperationFacts.From(rollback);
        var checkpointFacts = facts.WithRecoveryCheckpoint("checkpoint", "CHECKPOINT-DIGEST");
        var first = checkpointFacts.OrderedStepIds[0];

        await AppendAsync(journal, OperationTransition.Create(
            rollback.RollbackId, facts, 0, null, OperationState.RollbackPlanned,
            null, OccurredUtc));
        await AppendAsync(journal, OperationTransition.Create(
            rollback.RollbackId, facts, 1, OperationState.RollbackPlanned, OperationState.RollbackPrepared,
            null, OccurredUtc.AddMinutes(1), previousFacts: facts));
        await AppendAsync(journal, OperationTransition.Create(
            rollback.RollbackId, checkpointFacts, 2, OperationState.RollbackPrepared,
            OperationState.RollbackCheckpointCreated, null, OccurredUtc.AddMinutes(2), previousFacts: facts));
        await AppendAsync(journal, OperationTransition.Create(
            rollback.RollbackId, checkpointFacts, 3, OperationState.RollbackCheckpointCreated,
            OperationState.RollingBack, first, OccurredUtc.AddMinutes(3), previousFacts: checkpointFacts));
        var wrongStep = checkpointFacts.OrderedStepIds[1];
        var mismatchedApply = OperationTransition.Create(
            rollback.RollbackId, checkpointFacts, 4, OperationState.RollingBack,
            OperationState.RollbackApplied, wrongStep, OccurredUtc.AddMinutes(4), previousFacts: checkpointFacts);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            journal.CompareAndAppendAsync(mismatchedApply, CancellationToken.None).AsTask());
        var rollingBack = Assert.Single(await journal.ScanIncompleteAsync(CancellationToken.None));
        Assert.Equal(OperationState.RollingBack, rollingBack.State);
        Assert.Equal(first, rollingBack.StepId);
        Assert.Empty(rollingBack.AppliedStepIds);

        await AppendAsync(journal, OperationTransition.Create(
            rollback.RollbackId, checkpointFacts, 4, OperationState.RollingBack,
            OperationState.RollbackApplied, first, OccurredUtc.AddMinutes(4), previousFacts: checkpointFacts));
        await AppendAsync(journal, OperationTransition.Create(
            rollback.RollbackId, checkpointFacts, 5, OperationState.RollbackApplied,
            OperationState.RollbackVerified, first, OccurredUtc.AddMinutes(5), previousFacts: checkpointFacts));

        var premature = OperationTransition.Create(
            rollback.RollbackId, checkpointFacts, 6, OperationState.RollbackVerified,
            OperationState.RolledBack, null, OccurredUtc.AddMinutes(6), previousFacts: checkpointFacts);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            journal.CompareAndAppendAsync(premature, CancellationToken.None).AsTask());

        var incomplete = Assert.Single(await journal.ScanIncompleteAsync(CancellationToken.None));
        Assert.Equal(OperationState.RollbackVerified, incomplete.State);
        Assert.Equal([first], incomplete.AppliedStepIds);
    }

    [Fact]
    public async Task Incomplete_scan_can_resolve_an_exact_recovery_adapter_without_a_caller_plan()
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        var facts = DurableOperationFacts.From(fixture.Plan);
        var backedUpFacts = facts.WithBackupBinding(
            fixture.Plan.PlanId.ToString("N"),
            "BACKUP-DIGEST");

        await fixture.AppendAsync(fixture.Planned());
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            facts,
            1,
            OperationState.Planned,
            OperationState.Prepared,
            null,
            OccurredUtc.AddMinutes(1),
            previousFacts: facts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            2,
            OperationState.Prepared,
            OperationState.BackupCreated,
            null,
            OccurredUtc.AddMinutes(2),
            previousFacts: facts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            3,
            OperationState.BackupCreated,
            OperationState.Applying,
            "step-1",
            OccurredUtc.AddMinutes(3),
            previousFacts: backedUpFacts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            4,
            OperationState.Applying,
            OperationState.Applied,
            "step-1",
            OccurredUtc.AddMinutes(4),
            previousFacts: backedUpFacts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            5,
            OperationState.Applied,
            OperationState.Verified,
            "step-1",
            OccurredUtc.AddMinutes(5),
            previousFacts: backedUpFacts));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            backedUpFacts,
            6,
            OperationState.Verified,
            OperationState.Applying,
            "step-2",
            OccurredUtc.AddMinutes(6),
            previousFacts: backedUpFacts));

        var scanned = Assert.Single(await fixture.SecondJournal.ScanIncompleteAsync(
            CancellationToken.None));
        var request = DurableOperationRecoveryRequest.FromBoundary(scanned);
        var resolver = new AllowlistedRecoveryResolver("test.operation");
        var resolved = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("test.operation", request.CatalogOperationId);
        Assert.Equal(fixture.Plan.PlanId.ToString("N"), request.BackupBinding?.BackupId);
        Assert.Equal("BACKUP-DIGEST", request.BackupBinding?.BackupDigest);
        Assert.Equal(["step-1"], request.AppliedStepIds);
        Assert.NotNull(request.ActiveStep);
        Assert.Equal("step-2", request.ActiveStep.RecoveryKey);
        Assert.Equal(fixture.Plan.Steps[1].SourceFingerprint, request.ActiveStep.SourceFingerprint);
        Assert.Equal(fixture.Plan.Steps[1].ResultFingerprint, request.ActiveStep.ResultFingerprint);

        var unknownResolver = new AllowlistedRecoveryResolver("other.operation");
        Assert.Null(await unknownResolver.ResolveAsync(request, CancellationToken.None));

        var noBackupBoundary = DurableOperationBoundary.Create(
            fixture.Plan.PlanId,
            facts,
            revision: 1,
            OperationState.Planned,
            stepId: null,
            appliedStepIds: []);
        var noBackupRequest = DurableOperationRecoveryRequest.FromBoundary(noBackupBoundary);
        Assert.Null(await resolver.ResolveAsync(noBackupRequest, CancellationToken.None));

        var probe = await resolved.ProbeAsync(request.ActiveStep, CancellationToken.None);
        var rollback = await resolved.RollbackAsync(request.ActiveStep, CancellationToken.None);

        Assert.Equal(request.ActiveStep.SourceFingerprint, probe.CurrentFingerprint);
        Assert.Equal(StepResultKind.AlreadyRestored, rollback.Kind);
        Assert.Equal("step-2", resolver.LastRecoveryKey);
        Assert.Equal(fixture.Plan.PlanId.ToString("N"), resolver.LastBackupId);
    }

    [Fact]
    public async Task Event_chain_records_actor_and_previous_hash_without_sensitive_payload_fields()
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        var facts = DurableOperationFacts.From(fixture.Plan);
        await fixture.AppendAsync(fixture.Planned());
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            facts,
            1,
            OperationState.Planned,
            OperationState.Prepared,
            null,
            OccurredUtc.AddMinutes(1),
            previousFacts: facts));
        var files = Directory.EnumerateFiles(
                Path.Combine(fixture.Paths.GetOperationDirectory(fixture.OperationKey), "Transitions"),
                "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var firstJson = await File.ReadAllTextAsync(files[0]);
        var secondJson = await File.ReadAllTextAsync(files[1]);

        Assert.Contains("\"actor\":0", firstJson, StringComparison.Ordinal);
        Assert.Contains("\"previousEventHash\":null", firstJson, StringComparison.Ordinal);
        Assert.Contains("\"expectedFingerprint\"", firstJson, StringComparison.Ordinal);
        Assert.Contains("\"resultFingerprint\"", firstJson, StringComparison.Ordinal);
        Assert.Contains("\"errorCode\":0", firstJson, StringComparison.Ordinal);
        Assert.DoesNotContain("commandLine", secondJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rawException", secondJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", secondJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("target-step-1", secondJson, StringComparison.Ordinal);
        Assert.DoesNotContain("A deterministic persistence test plan.", secondJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"previousEventHash\":null", secondJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Incomplete_scan_returns_only_hash_verified_nonterminal_boundaries()
    {
        using var directory = new TemporaryDirectory();
        var incomplete = JournalFixture.Create(directory.Path);
        await incomplete.AppendAsync(incomplete.Planned());

        var completedPlan = TestPlan.Create(Guid.Parse("11f73954-2a1b-4194-8e89-19dd9e79af9d"));
        var completedJournal = new DurableOperationJournal(
            incomplete.Paths,
            DurableJournalActor.App);
        var completedFacts = DurableOperationFacts.From(completedPlan);
        await AppendAsync(completedJournal, OperationTransition.Create(
            completedPlan.PlanId,
            completedFacts,
            0,
            null,
            OperationState.Planned,
            null,
            OccurredUtc));
        await AppendAsync(completedJournal, OperationTransition.Create(
            completedPlan.PlanId,
            completedFacts,
            1,
            OperationState.Planned,
            OperationState.CanceledNoChanges,
            null,
            OccurredUtc.AddMinutes(1),
            previousFacts: completedFacts));

        var boundaries = await incomplete.SecondJournal.ScanIncompleteAsync(
            CancellationToken.None);

        var boundary = Assert.Single(boundaries);
        Assert.Equal(incomplete.Plan.PlanId, boundary.OperationId);
        Assert.Equal(OperationState.Planned, boundary.State);
    }

    [Fact]
    public async Task Aggregate_already_restored_rollback_rebuilds_terminal_and_is_not_scanned()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var journal = new DurableOperationJournal(paths, DurableJournalActor.App);
        var plan = TestPlan.Create();
        var rollback = RollbackPlan.Create(
            Guid.Parse("6e962729-7ced-449b-b475-ae8ff56ff393"),
            plan,
            BackupReceipt.Verified(
                plan.PlanId.ToString("N"),
                "BACKUP-DIGEST",
                plan.Digest,
                plan.SourceFingerprint,
                plan.SourceFingerprint),
            TestPlan.Fingerprint("applied"));
        var facts = DurableOperationFacts.From(rollback);

        await AppendAsync(journal, OperationTransition.Create(
            rollback.RollbackId,
            facts,
            0,
            null,
            OperationState.RollbackPlanned,
            null,
            OccurredUtc));
        await AppendAsync(journal, OperationTransition.Create(
            rollback.RollbackId,
            facts,
            1,
            OperationState.RollbackPlanned,
            OperationState.AlreadyRestored,
            null,
            OccurredUtc.AddMinutes(1),
            previousFacts: facts));
        await AppendAsync(journal, OperationTransition.Create(
            rollback.RollbackId,
            facts,
            2,
            OperationState.AlreadyRestored,
            OperationState.RolledBack,
            null,
            OccurredUtc.AddMinutes(2),
            previousFacts: facts));

        var boundary = await journal.ReadVerifiedBoundaryAsync(
            rollback.RollbackId,
            CancellationToken.None);
        var incomplete = await journal.ScanIncompleteAsync(CancellationToken.None);

        Assert.NotNull(boundary);
        Assert.Equal(OperationState.RolledBack, boundary.State);
        Assert.Equal(plan.SourceFingerprint, boundary.Facts.BackupFingerprint);
        Assert.Empty(incomplete);

        var alreadyRestoredPath = Directory.EnumerateFiles(
                Path.Combine(paths.GetOperationDirectory(rollback.RollbackId.ToString("N")), "Transitions"),
                "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ElementAt(1);
        var alreadyRestored = new JsonDocumentSerializer()
            .DeserializeAndValidate<OperationTransitionDocument>(
                File.ReadAllBytes(alreadyRestoredPath))
            .Payload;
        Assert.Equal(
            plan.SourceFingerprint,
            alreadyRestored.Metadata.ExpectedFingerprint.Value);
        Assert.Equal(
            plan.SourceFingerprint,
            alreadyRestored.Metadata.ResultFingerprint.Value);
    }

    [Fact]
    public async Task Manual_restore_point_artifact_reaches_completed_without_target_mutation_states()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var journal = new DurableOperationJournal(paths, DurableJournalActor.App);
        var plan = TestPlan.CreateManualRestorePoint();
        var facts = DurableOperationFacts.From(plan);
        var correlationId = Guid.Parse("948e582c-9ed5-4f88-91c9-85eb348c18e8");
        var beginReturnedAt = OccurredUtc.AddMinutes(3);
        var finalizationRequestedAt = OccurredUtc.AddMinutes(6);
        var requested = RestorePointTransitionFacts.Create(
            correlationId,
            "Winora restore point",
            TestPlan.Fingerprint("restore-inventory-before"),
            null,
            null,
            RestorePointApiStatus.NotCalled,
            RestorePointOwnershipStatus.NotChecked,
            RestorePointFinalizationMode.None,
            null,
            null,
            RestorePointApiStatus.NotCalled);
        var returned = RestorePointTransitionFacts.Create(
            correlationId,
            requested.Description,
            requested.PreBeginInventoryFingerprint,
            null,
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.NotChecked,
            RestorePointFinalizationMode.None,
            beginReturnedAt,
            null,
            RestorePointApiStatus.NotCalled);
        var begun = RestorePointTransitionFacts.Create(
            correlationId,
            requested.Description,
            requested.PreBeginInventoryFingerprint,
            TestPlan.Fingerprint("restore-inventory-after"),
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.VerifiedNewAndOwned,
            RestorePointFinalizationMode.None,
            beginReturnedAt,
            null,
            RestorePointApiStatus.NotCalled);
        var endRequested = RestorePointTransitionFacts.Create(
            correlationId,
            requested.Description,
            requested.PreBeginInventoryFingerprint,
            begun.PostBeginInventoryFingerprint,
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.VerifiedNewAndOwned,
            RestorePointFinalizationMode.Normal,
            beginReturnedAt,
            finalizationRequestedAt,
            RestorePointApiStatus.NotCalled);
        var ended = RestorePointTransitionFacts.Create(
            correlationId,
            requested.Description,
            requested.PreBeginInventoryFingerprint,
            begun.PostBeginInventoryFingerprint,
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.VerifiedNewAndOwned,
            RestorePointFinalizationMode.Normal,
            beginReturnedAt,
            finalizationRequestedAt,
            RestorePointApiStatus.Succeeded);

        await AppendAsync(journal, OperationTransition.Create(
            plan.PlanId, facts, 0, null, OperationState.Planned,
            null, OccurredUtc));
        await AppendAsync(journal, OperationTransition.Create(
            plan.PlanId, facts, 1, OperationState.Planned, OperationState.Prepared,
            null, OccurredUtc.AddMinutes(1), previousFacts: facts));
        await AppendAsync(journal, OperationTransition.Create(
            plan.PlanId, facts, 2, OperationState.Prepared, OperationState.RestorePointBeginRequested,
            null, OccurredUtc.AddMinutes(2), requested, previousFacts: facts));
        await AppendAsync(journal, OperationTransition.Create(
            plan.PlanId, facts, 3, OperationState.RestorePointBeginRequested,
            OperationState.RestorePointBeginReturnedUnverified, null,
            OccurredUtc.AddMinutes(3), returned, requested, facts));
        await AppendAsync(journal, OperationTransition.Create(
            plan.PlanId, facts, 4, OperationState.RestorePointBeginReturnedUnverified,
            OperationState.RestorePointBegun, null,
            OccurredUtc.AddMinutes(4), begun, returned, facts));
        await AppendAsync(journal, OperationTransition.Create(
            plan.PlanId, facts, 5, OperationState.RestorePointBegun,
            OperationState.RestorePointEndRequested, null,
            OccurredUtc.AddMinutes(5), endRequested, begun, facts));
        await AppendAsync(journal, OperationTransition.Create(
            plan.PlanId, facts, 6, OperationState.RestorePointEndRequested,
            OperationState.RestorePointEnded, null,
            OccurredUtc.AddMinutes(6), ended, endRequested, facts));
        await AppendAsync(journal, OperationTransition.Create(
            plan.PlanId, facts, 7, OperationState.RestorePointEnded,
            OperationState.Verified, plan.Steps[0].StepId,
            OccurredUtc.AddMinutes(7), ended, ended, facts));
        await AppendAsync(journal, OperationTransition.Create(
            plan.PlanId, facts, 8, OperationState.Verified,
            OperationState.Completed, null,
            OccurredUtc.AddMinutes(8), ended, ended, facts));

        var boundary = await journal.ReadVerifiedBoundaryAsync(
            plan.PlanId,
            CancellationToken.None);
        Assert.NotNull(boundary);
        Assert.Equal(OperationState.Completed, boundary.State);
        Assert.Equal([plan.Steps[0].StepId], boundary.AppliedStepIds);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(
                    Path.Combine(paths.GetOperationDirectory(plan.PlanId.ToString("N")), "Transitions"),
                    "*.json")
                .Select(path => new JsonDocumentSerializer()
                    .DeserializeAndValidate<OperationTransitionDocument>(File.ReadAllBytes(path))
                    .Payload.State),
            state => state is OperationState.Applying or OperationState.Applied);
    }

    [Fact]
    public async Task Read_rejects_a_junction_swapped_after_the_initial_directory_check()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        await fixture.AppendAsync(fixture.Planned());
        var transitionsDirectory = Path.Combine(
            fixture.Paths.GetOperationDirectory(fixture.OperationKey),
            "Transitions");
        var externalEvents = Path.Combine(outside.Path, "events");
        var swapped = false;
        var journal = new DurableOperationJournal(
            fixture.Paths,
            DurableJournalActor.App,
            beforeDirectoryLease: path =>
            {
                if (swapped || !StringComparer.OrdinalIgnoreCase.Equals(path, transitionsDirectory))
                {
                    return;
                }

                Directory.Move(transitionsDirectory, externalEvents);
                CreateJunction(transitionsDirectory, externalEvents);
                swapped = true;
            });

        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                journal.ReadVerifiedBoundaryAsync(
                    fixture.Plan.PlanId,
                    CancellationToken.None).AsTask());
        }
        finally
        {
            if (swapped && Directory.Exists(transitionsDirectory))
            {
                Directory.Delete(transitionsDirectory);
            }
        }
    }

    [Fact]
    public async Task Reconstruction_rejects_duplicate_revisions_and_transition_ids()
    {
        using var revisionDirectory = new TemporaryDirectory();
        var revisionFixture = JournalFixture.Create(revisionDirectory.Path);
        await revisionFixture.AppendAsync(revisionFixture.Planned());
        var original = FirstTransitionPath(revisionFixture);
        File.Copy(
            original,
            Path.Combine(Path.GetDirectoryName(original)!, $"1-{Guid.NewGuid():N}.json"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            revisionFixture.SecondJournal.ReadVerifiedBoundaryAsync(
                revisionFixture.Plan.PlanId,
                CancellationToken.None).AsTask());

        using var idDirectory = new TemporaryDirectory();
        var idFixture = JournalFixture.Create(idDirectory.Path);
        await idFixture.AppendAsync(idFixture.Planned());
        original = FirstTransitionPath(idFixture);
        var transitionId = Path.GetFileNameWithoutExtension(original).Split('-', 2)[1];
        File.Copy(
            original,
            Path.Combine(Path.GetDirectoryName(original)!, $"2-{transitionId}.json"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            idFixture.SecondJournal.ReadVerifiedBoundaryAsync(
                idFixture.Plan.PlanId,
                CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("state")]
    [InlineData("error-code")]
    [InlineData("risk")]
    public async Task Reconstruction_rejects_unknown_persisted_enum_values(string field)
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        await fixture.AppendAsync(fixture.Planned());
        var path = FirstTransitionPath(fixture);
        RewriteTransition(path, payload => field switch
        {
            "actor" => payload with { Actor = (DurableJournalActor)999 },
            "state" => payload with { State = (OperationState)999 },
            "error-code" => payload with
            {
                Metadata = payload.Metadata with
                {
                    ErrorCode = (DurableOperationErrorCode)999,
                },
            },
            "risk" => payload with
            {
                Facts = payload.Facts with { Risk = (RiskLevel)999 },
            },
            _ => throw new InvalidOperationException(),
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.SecondJournal.ReadVerifiedBoundaryAsync(
                fixture.Plan.PlanId,
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Transition_identity_is_unique_and_matches_filename_envelope_and_payload()
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        var facts = DurableOperationFacts.From(fixture.Plan);
        await fixture.AppendAsync(fixture.Planned());
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            facts,
            1,
            OperationState.Planned,
            OperationState.Prepared,
            null,
            OccurredUtc.AddMinutes(1),
            previousFacts: facts));
        var serializer = new JsonDocumentSerializer();
        var identities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(
                         fixture.Paths.GetOperationDirectory(fixture.OperationKey),
                         "Transitions"),
                     "*.json"))
        {
            var fileId = Path.GetFileNameWithoutExtension(path).Split('-', 2)[1];
            var envelope = serializer.DeserializeAndValidate<OperationTransitionDocument>(
                File.ReadAllBytes(path));
            Assert.Equal(fileId, envelope.DocumentId);
            Assert.Equal(fileId, envelope.Payload.TransitionId);
            Assert.True(identities.Add(fileId));
        }

        Assert.Equal(2, identities.Count);
    }

    [Fact]
    public async Task Two_processes_competing_for_revision_zero_commit_exactly_one_event()
    {
        using var directory = new TemporaryDirectory();
        var readyOne = Path.Combine(directory.Path, "ready-one");
        var readyTwo = Path.Combine(directory.Path, "ready-two");
        var release = Path.Combine(directory.Path, "release");
        using var first = StartJournalProcess(directory.Path, readyOne, release);
        using var second = StartJournalProcess(directory.Path, readyTwo, release);
        await WaitForFilesAsync([readyOne, readyTwo]);
        await File.WriteAllTextAsync(release, "go");

        await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync());

        Assert.Equal([0, 2], new[] { first.ExitCode, second.ExitCode }.Order().ToArray());
        var plan = TestPlan.Create();
        var paths = new WinoraDataPaths(directory.Path);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(paths.GetOperationDirectory(plan.PlanId.ToString("N")), "Transitions"),
            "*.json"));
    }

    [Fact]
    public async Task Storage_catalog_distinguishes_protected_incomplete_history_and_deletes_only_verified_terminal_history()
    {
        using var directory = new TemporaryDirectory();
        var incomplete = JournalFixture.Create(directory.Path);
        await incomplete.AppendAsync(incomplete.Planned());
        var completedPlan = TestPlan.Create(Guid.Parse("22f73954-2a1b-4194-8e89-19dd9e79af9d"));
        var completedJournal = new DurableOperationJournal(
            incomplete.Paths,
            DurableJournalActor.App);
        var facts = DurableOperationFacts.From(completedPlan);
        await AppendAsync(completedJournal, OperationTransition.Create(
            completedPlan.PlanId,
            facts,
            0,
            null,
            OperationState.Planned,
            null,
            OccurredUtc));
        await AppendAsync(completedJournal, OperationTransition.Create(
            completedPlan.PlanId,
            facts,
            1,
            OperationState.Planned,
            OperationState.CanceledNoChanges,
            null,
            OccurredUtc.AddMinutes(1),
            previousFacts: facts));

        var catalog = await incomplete.Journal.ScanStorageCatalogAsync(CancellationToken.None);

        var protectedEntry = Assert.Single(catalog, item => item.OperationId == incomplete.Plan.PlanId);
        Assert.True(protectedEntry.IsRecoveryProtected);
        Assert.False(protectedEntry.IsTerminal);
        var terminalEntry = Assert.Single(catalog, item => item.OperationId == completedPlan.PlanId);
        Assert.True(terminalEntry.IsTerminal);
        Assert.False(terminalEntry.IsRecoveryProtected);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            incomplete.Journal.DeleteVerifiedTerminalAsync(
                protectedEntry,
                CancellationToken.None).AsTask());

        var deleted = await incomplete.Journal.DeleteVerifiedTerminalAsync(
            terminalEntry,
            CancellationToken.None);

        Assert.True(deleted);
        Assert.False(Directory.Exists(
            incomplete.Paths.GetOperationDirectory(completedPlan.PlanId.ToString("N"))));
    }

    [Fact]
    public async Task Retention_delete_rejects_operation_root_replaced_after_catalog_capture()
    {
        using var directory = new TemporaryDirectory();
        var fixture = JournalFixture.Create(directory.Path);
        var facts = DurableOperationFacts.From(fixture.Plan);
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            facts,
            expectedRevision: 0,
            expectedState: null,
            OperationState.Planned,
            stepId: null,
            OccurredUtc));
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            facts,
            expectedRevision: 1,
            OperationState.Planned,
            OperationState.CanceledNoChanges,
            stepId: null,
            OccurredUtc.AddMinutes(1),
            previousFacts: facts));
        var expected = Assert.Single(await fixture.Journal.ScanStorageCatalogAsync(
            CancellationToken.None));
        var operationDirectory = fixture.Paths.GetOperationDirectory(fixture.OperationKey);
        var displaced = operationDirectory + "-displaced";
        Directory.Move(operationDirectory, displaced);
        CopyDirectory(displaced, operationDirectory);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Journal.DeleteVerifiedTerminalAsync(
                expected,
                CancellationToken.None).AsTask());

        Assert.True(Directory.Exists(operationDirectory));
        Assert.True(Directory.Exists(displaced));
    }

    [Fact]
    public async Task Slow_immutable_history_read_does_not_hold_the_global_persistence_mutex()
    {
        using var directory = new TemporaryDirectory();
        using var observer = new BlockingTransitionReadObserver();
        var fixture = JournalFixture.Create(directory.Path);
        await fixture.AppendAsync(fixture.Planned());
        var facts = DurableOperationFacts.From(fixture.Plan);
        await fixture.AppendAsync(OperationTransition.Create(
            fixture.Plan.PlanId,
            facts,
            1,
            OperationState.Planned,
            OperationState.Prepared,
            null,
            OccurredUtc.AddMinutes(1),
            previousFacts: facts));
        var blockedDocuments = new AtomicJsonFile(
            fixture.Paths,
            validatedFileObserver: observer);
        var blockedJournal = new DurableOperationJournal(
            fixture.Paths,
            DurableJournalActor.App,
            blockedDocuments);

        var historyRead = Task.Run(async () =>
            await blockedJournal.ReadVerifiedBoundaryAsync(
                fixture.Plan.PlanId,
                CancellationToken.None));
        observer.WaitUntilBlocked();
        var unrelatedDocuments = new AtomicJsonFile(
            fixture.Paths,
            (JsonDocumentSerializer?)null,
            null);
        var unrelatedWrite = unrelatedDocuments.CreateNewAsync(
            fixture.Paths.GetJournalEventDocument("unrelated-event"),
            new ValuePayload(7),
            CancellationToken.None).AsTask();

        var first = await Task.WhenAny(unrelatedWrite, Task.Delay(TimeSpan.FromSeconds(2)));
        observer.Release();
        await historyRead;
        await unrelatedWrite;

        Assert.Same(unrelatedWrite, first);
    }

    [Fact]
    public async Task Journal_publish_and_temp_cleanup_failures_preserve_primary_failure_first()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var documents = new AtomicJsonFile(
            paths,
            publisher: new ThrowBeforePublishPublisher(),
            cleanup: new ThrowingAtomicFileCleanup());
        var journal = new DurableOperationJournal(
            paths,
            DurableJournalActor.App,
            documents);
        var plan = TestPlan.Create();
        var transition = OperationTransition.Create(
            plan.PlanId,
            DurableOperationFacts.From(plan),
            0,
            null,
            OperationState.Planned,
            null,
            OccurredUtc);

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            journal.CompareAndAppendAsync(
                transition,
                CancellationToken.None).AsTask());

        Assert.IsType<InjectedStorageException>(failure.InnerExceptions[0]);
        Assert.IsType<InjectedCleanupException>(failure.InnerExceptions[1]);
        Directory.Move(paths.OperationsDirectory, paths.OperationsDirectory + "-moved");
    }

    [Fact]
    public async Task Projection_aggregate_failure_does_not_hide_an_already_durable_transition()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var documents = new AtomicJsonFile(
            paths,
            publisher: new FailSecondPublicationPublisher(),
            cleanup: new ThrowOnFirstCleanup());
        var journal = new DurableOperationJournal(
            paths,
            DurableJournalActor.App,
            documents);
        var plan = TestPlan.Create();

        var result = await journal.CompareAndAppendAsync(
            OperationTransition.Create(
                plan.PlanId,
                DurableOperationFacts.From(plan),
                0,
                null,
                OperationState.Planned,
                null,
                OccurredUtc),
            CancellationToken.None);

        Assert.True(result.IsDurable);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(
                paths.GetOperationDirectory(plan.PlanId.ToString("N")),
                "Transitions"),
            "*.json"));
    }

    private static async ValueTask AppendAsync(
        DurableOperationJournal journal,
        OperationTransition transition)
    {
        var result = await journal.CompareAndAppendAsync(transition, CancellationToken.None);
        Assert.True(result.IsDurable);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var sourceDirectory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, sourceDirectory)));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.Copy(
                sourceFile,
                Path.Combine(destination, Path.GetRelativePath(source, sourceFile)));
        }
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo(
            "cmd.exe",
            $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Unable to start junction helper.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }

    private static string FirstTransitionPath(JournalFixture fixture) =>
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(fixture.Paths.GetOperationDirectory(fixture.OperationKey), "Transitions"),
            "*.json"));

    private static void RewriteTransition(
        string path,
        Func<OperationTransitionDocument, OperationTransitionDocument> rewrite)
    {
        var serializer = new JsonDocumentSerializer();
        var envelope = serializer.DeserializeAndValidate<OperationTransitionDocument>(
            File.ReadAllBytes(path));
        var changed = rewrite(envelope.Payload);
        File.WriteAllBytes(
            path,
            serializer.Serialize(serializer.CreateEnvelope(
                envelope.DocumentId,
                envelope.CreatedUtc,
                changed)));
    }

    private static Process StartJournalProcess(
        string root,
        string readyPath,
        string releasePath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(ProcessHostMarker).Assembly.Location);
        startInfo.ArgumentList.Add("journal-append");
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add(readyPath);
        startInfo.ArgumentList.Add(releasePath);
        return Process.Start(startInfo) ??
            throw new InvalidOperationException("Unable to start journal process helper.");
    }

    private static async Task WaitForFilesAsync(IReadOnlyList<string> paths)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!paths.All(File.Exists))
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed record JournalFixture(
        WinoraDataPaths Paths,
        DurableOperationJournal Journal,
        DurableOperationJournal SecondJournal,
        ChangePlan Plan)
    {
        internal string OperationKey => Plan.PlanId.ToString("N");

        internal static JournalFixture Create(string root)
        {
            var paths = new WinoraDataPaths(root);
            var plan = TestPlan.Create();
            return new JournalFixture(
                paths,
                new DurableOperationJournal(paths, DurableJournalActor.App),
                new DurableOperationJournal(paths, DurableJournalActor.App),
                plan);
        }

        internal OperationTransition Planned(DateTimeOffset? occurredUtc = null) =>
            OperationTransition.Create(
                Plan.PlanId,
                DurableOperationFacts.From(Plan),
                0,
                null,
                OperationState.Planned,
                null,
                occurredUtc ?? OccurredUtc);

        internal async ValueTask AppendAsync(OperationTransition transition)
        {
            var result = await Journal.CompareAndAppendAsync(transition, CancellationToken.None);
            Assert.True(result.IsDurable);
        }
    }
}

internal sealed class BlockingTransitionReadObserver : IValidatedFileObserver, IDisposable
{
    private readonly ManualResetEventSlim _blocked = new();
    private readonly ManualResetEventSlim _release = new();
    private int _hasBlocked;

    public void OnValidated(
        string path,
        ValidatedFileIdentity identity,
        ValidatedFileUse use)
    {
        if (use != ValidatedFileUse.PublicRead ||
            !path.Contains($"{Path.DirectorySeparatorChar}Transitions{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            Interlocked.Exchange(ref _hasBlocked, 1) != 0)
        {
            return;
        }

        _blocked.Set();
        if (!_release.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("The test did not release the blocked history read.");
        }
    }

    public void WaitUntilBlocked()
    {
        if (!_blocked.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("The immutable history read did not reach the test hook.");
        }
    }

    public void Release() => _release.Set();

    public void Dispose()
    {
        _release.Set();
        _blocked.Dispose();
        _release.Dispose();
    }
}

internal sealed class FailSecondPublicationPublisher : IWriteThroughPublisher
{
    private readonly WriteThroughPublisher _inner = new();
    private int _calls;

    public ValueTask PublishNewAsync(
        ValidatedFileHandle temporaryFile,
        string finalPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) == 2)
        {
            throw new InjectedProjectionStorageException();
        }

        return _inner.PublishNewAsync(
            temporaryFile,
            finalPath,
            expectedHash,
            cancellationToken);
    }

    public ValueTask ReplaceProjectionAsync(
        ValidatedFileHandle temporaryFile,
        ValidatedFileHandle targetFile,
        string finalPath,
        ValidatedFileHandle? existingLastKnownGoodFile,
        string lastKnownGoodPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) == 2)
        {
            throw new InjectedProjectionStorageException();
        }

        return _inner.ReplaceProjectionAsync(
            temporaryFile,
            targetFile,
            finalPath,
            existingLastKnownGoodFile,
            lastKnownGoodPath,
            expectedHash,
            cancellationToken);
    }
}

internal sealed class ThrowOnFirstCleanup : IAtomicFileCleanup
{
    private int _calls;

    public void Delete(ValidatedFileHandle file)
    {
        if (Interlocked.Increment(ref _calls) == 1)
        {
            throw new InjectedCleanupException();
        }

        new AtomicFileCleanup().Delete(file);
    }
}

internal sealed class InjectedProjectionStorageException : IOException;

internal sealed class AllowlistedRecoveryResolver(string allowlistedOperationId)
    : IOperationRecoveryResolver, IResolvedOperationRecovery
{
    private DurableOperationRecoveryRequest? _request;

    public string CatalogOperationId => allowlistedOperationId;

    internal string? LastRecoveryKey { get; private set; }

    internal string? LastBackupId { get; private set; }

    public ValueTask<IResolvedOperationRecovery?> ResolveAsync(
        DurableOperationRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.Ordinal.Equals(request.CatalogOperationId, allowlistedOperationId) ||
            request.BackupBinding is null)
        {
            return ValueTask.FromResult<IResolvedOperationRecovery?>(null);
        }

        _request = request;
        return ValueTask.FromResult<IResolvedOperationRecovery?>(this);
    }

    public ValueTask<OperationCapability> ProbeAsync(
        DurableStepRecoveryDescriptor step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecordBinding(step);
        return ValueTask.FromResult(new OperationCapability(
            SupportStatus.Supported,
            PrivilegeRequirement.StandardUser,
            step.SourceFingerprint,
            IsApiAvailable: true,
            IsWritable: true,
            IsBackupAvailable: true,
            IsVerificationAvailable: true,
            IsRollbackAvailable: true,
            IsConditionalMutationAvailable: true,
            BlockReason: null));
    }

    public ValueTask<VerificationResult> VerifyAsync(
        DurableStepRecoveryDescriptor step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecordBinding(step);
        return ValueTask.FromResult(VerificationResult.Passed(step.ResultFingerprint));
    }

    public ValueTask<StepResult> RollbackAsync(
        DurableStepRecoveryDescriptor step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecordBinding(step);
        return ValueTask.FromResult(StepResult.AlreadyRestored(step.SourceFingerprint));
    }

    private void RecordBinding(DurableStepRecoveryDescriptor step)
    {
        LastRecoveryKey = step.RecoveryKey;
        LastBackupId = _request?.BackupBinding?.BackupId;
    }
}

internal static class TestPlan
{
    internal static StateFingerprint Fingerprint(string value) =>
        new("SHA-256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))));

    internal static ChangePlan Create(Guid? planId = null) =>
        ChangePlan.Create(
            planId ?? Guid.Parse("5f739542-2a1b-4194-8e89-19dd9e79af9d"),
            "test.operation",
            "Test",
            "Test change",
            "A deterministic persistence test plan.",
            [
                Step("step-1", "source-1", "result-1"),
                Step("step-2", "source-2", "result-2"),
            ],
            RiskLevel.Low,
            PrivilegeRequirement.StandardUser,
            RollbackCapability.Full,
            RestartRequirement.None,
            SupportStatus.Supported,
            Fingerprint("source-plan"),
            new Uri("https://learn.microsoft.com/windows/"),
            BackupRequirement.Required,
            requiresRestorePoint: false);

    internal static ChangePlan CreateManualRestorePoint() =>
        ChangePlan.Create(
            Guid.Parse("a44e7ee7-f12d-4822-a783-d047741dd94a"),
            "safety.restore-point",
            "Safety",
            "Create restore point",
            "Creates one Winora-owned non-mutating safety artifact.",
            [Step("restore-point", "restore-not-started", "restore-completed")],
            RiskLevel.Informational,
            PrivilegeRequirement.Administrator,
            RollbackCapability.NotApplicable,
            RestartRequirement.None,
            SupportStatus.SupportedWithElevation,
            Fingerprint("restore-plan-source"),
            new Uri("https://learn.microsoft.com/windows/win32/sr/system-restore-api"),
            BackupRequirement.NotApplicable,
            requiresRestorePoint: false,
            ChangePlanKind.ManualRestorePointArtifact);

    private static ChangeStep Step(string id, string source, string result) =>
        new(
            id,
            new OperationTarget($"target-{id}"),
            new DisplayValue("text", source),
            new DisplayValue("text", result),
            Fingerprint(source),
            Fingerprint(result),
            new VerificationProbe($"verify-{id}", result));
}
