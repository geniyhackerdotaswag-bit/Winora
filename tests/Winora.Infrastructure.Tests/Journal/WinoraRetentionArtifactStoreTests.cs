using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Core.Journal;
using Winora.Infrastructure.Backups;
using Winora.Infrastructure.Journal;
using Winora.Infrastructure.Operations;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Tests.Operations;
using Xunit;
using TestContext = Winora.Infrastructure.Tests.Journal.JournalTestContext;

namespace Winora.Infrastructure.Tests.Journal;

public sealed class WinoraRetentionArtifactStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Production_store_resumes_after_operation_tree_was_deleted_before_state_advance()
    {
        using var fixture = ProductionFixture.Create(Now);
        var operationId = Guid.NewGuid();
        var plan = TestPlan.Create(operationId);
        var facts = DurableOperationFacts.From(plan);
        await AppendTransitionAsync(
            fixture.Operations,
            OperationTransition.Create(
                operationId,
                facts,
                expectedRevision: 0,
                expectedState: null,
                OperationState.Planned,
                stepId: null,
                Now.AddDays(-100)));
        await AppendTransitionAsync(
            fixture.Operations,
            OperationTransition.Create(
                operationId,
                facts,
                expectedRevision: 1,
                OperationState.Planned,
                OperationState.CanceledNoChanges,
                stepId: null,
                Now.AddDays(-100).AddMinutes(1),
                previousFacts: facts));
        var catalogEntry = Assert.Single(
            await fixture.Operations.ScanStorageCatalogAsync(
                TestContext.Current.CancellationToken));
        var selection = new RetentionArtifactSelection(
            new RetentionOperationIdentity(
                catalogEntry.OperationId,
                catalogEntry.Revision,
                catalogEntry.State,
                catalogEntry.LastEventHash,
                catalogEntry.TerminalOccurredAtUtc,
                catalogEntry.PlanDigest,
                catalogEntry.BackupId,
                catalogEntry.BackupDigest,
                catalogEntry.RootVolumeSerialNumber,
                catalogEntry.RootFileIndex,
                catalogEntry.IsTerminal,
                catalogEntry.IsRecoveryProtected),
            Backup: null,
            ActionEvents: [],
            RetentionLinkedStateSnapshot.Create([catalogEntry], operationId));
        var request = new ActionJournalRetentionRequest(
            operationId,
            new HashSet<Guid>(),
            TimeSpan.FromDays(365),
            25_000);
        var transactionId = Guid.NewGuid();
        var firstLease = new TestRetentionLease(
            transactionId,
            epoch: 1,
            isRecoveryTakeover: false);
        var boundary = await fixture.Lifecycle.CreateApprovedAsync(
            transactionId,
            firstLease,
            request,
            selection,
            TestContext.Current.CancellationToken);
        boundary = await fixture.Lifecycle.AdvanceAsync(
            boundary,
            RetentionLifecycleState.DeletingOperation,
            firstLease,
            TestContext.Current.CancellationToken);

        Assert.True(await fixture.Store.DeleteOperationAsync(
            boundary,
            TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(
            fixture.Paths.GetOperationDirectory(operationId.ToString("N"))));

        var restarted = new ActionJournalRetentionCoordinator(
            fixture.Journal,
            new DurableRetentionJournal(fixture.Paths, fixture.Clock),
            new WinoraRetentionArtifactStore(
                new DurableOperationJournal(
                    fixture.Paths,
                    DurableJournalActor.App,
                    fixture.Clock),
                new BackupRepository(
                    fixture.Paths,
                    new NeverCaptureBackupProvider(),
                    fixture.Clock),
                fixture.Journal),
            fixture.Clock);
        var result = await restarted.ResumeAsync(
            new TestRetentionLease(
                transactionId,
                epoch: 2,
                isRecoveryTakeover: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(RetentionLifecycleState.Completed, result.State);
        Assert.True(result.Resumed);
    }

    [Fact]
    public async Task Production_store_revalidates_lease_adjacent_to_each_action_event_delete()
    {
        using var fixture = ProductionFixture.Create(Now.AddDays(-400));
        await fixture.Journal.AppendAsync(
            Draft(Guid.NewGuid()),
            TestContext.Current.CancellationToken);
        await fixture.Journal.AppendAsync(
            Draft(Guid.NewGuid()),
            TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromDays(400));
        var transactionId = Guid.NewGuid();
        var lease = new SequenceRetentionLease(transactionId, [true, true, false]);
        var coordinator = new ActionJournalRetentionCoordinator(
            fixture.Journal,
            fixture.Lifecycle,
            fixture.Store,
            fixture.Clock);

        await Assert.ThrowsAsync<RetentionLeaseLostException>(async () =>
            await coordinator.RunAsync(
                lease,
                new ActionJournalRetentionRequest(
                    completedOperationId: null,
                    new HashSet<Guid>(),
                    TimeSpan.FromDays(365),
                    maximumEventCount: 25_000),
                TestContext.Current.CancellationToken));

        Assert.Equal(3, lease.RevalidationCount);
        var remaining = await fixture.Journal.ReadAllAsync(
            TestContext.Current.CancellationToken);
        Assert.Single(remaining, entry => entry.Status == ActionJournalStatus.Succeeded);
        Assert.Single(remaining, entry => entry.Status == ActionJournalStatus.RetentionApproved);
        Assert.DoesNotContain(
            remaining,
            entry => entry.Status == ActionJournalStatus.RetentionCompleted);
        var boundary = await fixture.Lifecycle.ReadAsync(
            transactionId,
            TestContext.Current.CancellationToken);
        Assert.Equal(RetentionLifecycleState.DeletingActionEvents, boundary.State);
    }

    [Fact]
    public async Task Authoritative_linked_catalog_rejects_an_omitted_caller_operation_id()
    {
        using var fixture = ProductionFixture.Create(Now);
        var linkedOperationId = Guid.NewGuid();
        var store = new WinoraRetentionArtifactStore(
            new StubOperationRetentionStore([LinkedCatalogEntry(linkedOperationId)]),
            new StubBackupRetentionStore([]),
            fixture.Journal);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CaptureAsync(
                new ActionJournalRetentionRequest(
                    completedOperationId: null,
                    new HashSet<Guid>(),
                    TimeSpan.FromDays(365),
                    25_000),
                Now,
                ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Authoritative_linked_catalog_rejects_an_invented_caller_operation_id()
    {
        using var fixture = ProductionFixture.Create(Now);
        var store = new WinoraRetentionArtifactStore(
            new StubOperationRetentionStore([]),
            new StubBackupRetentionStore([]),
            fixture.Journal);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CaptureAsync(
                new ActionJournalRetentionRequest(
                    completedOperationId: null,
                    new HashSet<Guid> { Guid.NewGuid() },
                    TimeSpan.FromDays(365),
                    25_000),
                Now,
                ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Durable_linked_state_hash_rejects_a_contradictory_sorted_operation_id()
    {
        var transactionId = Guid.NewGuid();
        var request = new ActionJournalRetentionRequest(
            completedOperationId: null,
            new HashSet<Guid>(),
            TimeSpan.FromDays(365),
            25_000);
        var intent = RetentionIntentDocument.Create(
            transactionId,
            Now,
            new TestRetentionLease(
                transactionId,
                epoch: 1,
                isRecoveryTakeover: false),
            request,
            RetentionArtifactSelection.Empty);
        var contradictory = intent with
        {
            LinkedChangeOperationIds = Array.AsReadOnly(new[] { Guid.NewGuid() }),
        };

        Assert.Throws<InvalidDataException>(() =>
            RetentionMaintenanceSchema.Validate(contradictory));
    }

    [Fact]
    public async Task Linked_catalog_change_before_intent_publication_fails_closed()
    {
        using var fixture = ProductionFixture.Create(Now);
        var operations = new SequenceOperationRetentionStore(
            [],
            [LinkedCatalogEntry(Guid.NewGuid())]);
        var store = new WinoraRetentionArtifactStore(
            operations,
            new StubBackupRetentionStore([]),
            fixture.Journal);
        var transactionId = Guid.NewGuid();
        var coordinator = new ActionJournalRetentionCoordinator(
            fixture.Journal,
            fixture.Lifecycle,
            store,
            fixture.Clock);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await coordinator.RunAsync(
                new TestRetentionLease(
                    transactionId,
                    epoch: 1,
                    isRecoveryTakeover: false),
                new ActionJournalRetentionRequest(
                    completedOperationId: null,
                    new HashSet<Guid>(),
                    TimeSpan.FromDays(365),
                    25_000),
                TestContext.Current.CancellationToken));

        Assert.Equal(2, operations.ScanCount);
        Assert.False(File.Exists(
            fixture.Paths.GetRetentionIntentFile(transactionId.ToString("N"))));
    }

    [Fact]
    public async Task Resume_preserves_rollback_failed_event_when_authoritative_link_appears()
    {
        using var fixture = ProductionFixture.Create(Now.AddDays(-400));
        var linkedOperationId = Guid.NewGuid();
        await fixture.Journal.AppendAsync(
            RollbackFailedDraft(linkedOperationId),
            TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromDays(400));
        var operations = new MutableOperationRetentionStore();
        var store = new WinoraRetentionArtifactStore(
            operations,
            new StubBackupRetentionStore([]),
            fixture.Journal);
        var request = new ActionJournalRetentionRequest(
            completedOperationId: null,
            new HashSet<Guid>(),
            TimeSpan.FromDays(365),
            maximumEventCount: 25_000);
        var selection = await store.CaptureAsync(
            request,
            fixture.Clock.GetUtcNow(),
            ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
            TestContext.Current.CancellationToken);
        await store.VerifyLinkedStateAsync(
            selection.LinkedState,
            TestContext.Current.CancellationToken);
        var selectedEvent = Assert.Single(selection.ActionEvents);
        var transactionId = Guid.NewGuid();
        var firstLease = new TestRetentionLease(
            transactionId,
            epoch: 1,
            isRecoveryTakeover: false);
        var boundary = await PrepareActionDeletionAsync(
            fixture.Lifecycle,
            firstLease,
            request,
            selection);
        operations.Catalog = [LinkedCatalogEntry(linkedOperationId)];
        var coordinator = new ActionJournalRetentionCoordinator(
            fixture.Journal,
            fixture.Lifecycle,
            store,
            fixture.Clock);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await coordinator.ResumeAsync(
                new TestRetentionLease(
                    transactionId,
                    epoch: 2,
                    isRecoveryTakeover: true),
                TestContext.Current.CancellationToken));

        Assert.True(File.Exists(
            fixture.Paths.GetJournalEventFile(selectedEvent.EventId)));
        var persisted = await fixture.Lifecycle.ReadAsync(
            transactionId,
            TestContext.Current.CancellationToken);
        Assert.Equal(RetentionLifecycleState.DeletingActionEvents, persisted.State);
    }

    [Fact]
    public async Task Action_event_loop_rechecks_authoritative_links_before_each_failure_delete()
    {
        using var fixture = ProductionFixture.Create(Now.AddDays(-400));
        var first = await fixture.Journal.AppendAsync(
            RollbackFailedDraft(Guid.NewGuid()),
            TestContext.Current.CancellationToken);
        var second = await fixture.Journal.AppendAsync(
            RollbackFailedDraft(Guid.NewGuid()),
            TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromDays(400));
        var ordered = new[] { first, second }
            .OrderBy(entry => entry.EventId, StringComparer.Ordinal)
            .ToArray();
        var operations = new SequenceOperationRetentionStore(
            [],
            [],
            [],
            [],
            [LinkedCatalogEntry(ordered[1].OperationId)]);
        var store = new WinoraRetentionArtifactStore(
            operations,
            new StubBackupRetentionStore([]),
            fixture.Journal);
        var request = new ActionJournalRetentionRequest(
            completedOperationId: null,
            new HashSet<Guid>(),
            TimeSpan.FromDays(365),
            maximumEventCount: 25_000);
        var selection = await store.CaptureAsync(
            request,
            fixture.Clock.GetUtcNow(),
            ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
            TestContext.Current.CancellationToken);
        await store.VerifyLinkedStateAsync(
            selection.LinkedState,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, selection.ActionEvents.Count);
        var transactionId = Guid.NewGuid();
        var firstLease = new TestRetentionLease(
            transactionId,
            epoch: 1,
            isRecoveryTakeover: false);
        _ = await PrepareActionDeletionAsync(
            fixture.Lifecycle,
            firstLease,
            request,
            selection);
        var coordinator = new ActionJournalRetentionCoordinator(
            fixture.Journal,
            fixture.Lifecycle,
            store,
            fixture.Clock);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await coordinator.ResumeAsync(
                new TestRetentionLease(
                    transactionId,
                    epoch: 2,
                    isRecoveryTakeover: true),
                TestContext.Current.CancellationToken));

        Assert.Equal(5, operations.ScanCount);
        Assert.False(File.Exists(
            fixture.Paths.GetJournalEventFile(ordered[0].EventId)));
        Assert.True(File.Exists(
            fixture.Paths.GetJournalEventFile(ordered[1].EventId)));
        var rebuilt = await fixture.Journal.RebuildIndexAsync(
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(rebuilt.Events, item => item.EventId == ordered[0].EventId);
        Assert.Contains(rebuilt.Events, item => item.EventId == ordered[1].EventId);
        var persisted = await fixture.Lifecycle.ReadAsync(
            transactionId,
            TestContext.Current.CancellationToken);
        Assert.Equal(RetentionLifecycleState.DeletingActionEvents, persisted.State);
    }

    [Fact]
    public void Retention_schema_rejects_completed_operation_identity_mismatch()
    {
        var selectedOperationId = Guid.NewGuid();
        var selectedCatalog = LinkedCatalogEntry(selectedOperationId) with
        {
            State = OperationState.CanceledNoChanges,
            IsTerminal = true,
            IsRecoveryProtected = false,
        };
        var operation = new RetentionOperationIdentity(
            selectedCatalog.OperationId,
            selectedCatalog.Revision,
            selectedCatalog.State,
            selectedCatalog.LastEventHash,
            selectedCatalog.TerminalOccurredAtUtc,
            selectedCatalog.PlanDigest,
            selectedCatalog.BackupId,
            selectedCatalog.BackupDigest,
            selectedCatalog.RootVolumeSerialNumber,
            selectedCatalog.RootFileIndex,
            selectedCatalog.IsTerminal,
            selectedCatalog.IsRecoveryProtected);
        var transactionId = Guid.NewGuid();
        var lease = new TestRetentionLease(
            transactionId,
            epoch: 1,
            isRecoveryTakeover: false);
        var intent = RetentionIntentDocument.Create(
            transactionId,
            Now,
            lease,
            new ActionJournalRetentionRequest(
                selectedOperationId,
                new HashSet<Guid>(),
                TimeSpan.FromDays(365),
                25_000),
            new RetentionArtifactSelection(
                operation,
                Backup: null,
                ActionEvents: [],
                RetentionLinkedStateSnapshot.Create(
                    [selectedCatalog],
                    selectedOperationId)));
        var wrongCompletedOperationId = Guid.NewGuid();
        var wrongLinkedState = RetentionLinkedStateSnapshot.Create(
            [],
            wrongCompletedOperationId);
        var contradictory = intent with
        {
            CompletedOperationId = wrongCompletedOperationId,
            LinkedStateSchemaVersion = wrongLinkedState.SchemaVersion,
            LinkedCatalogSha256 = wrongLinkedState.CatalogSha256,
            LinkedChangeOperationIds = wrongLinkedState.LinkedOperationIds,
            LinkedStateSha256 = wrongLinkedState.SnapshotSha256,
        };

        Assert.Throws<InvalidDataException>(() =>
            RetentionMaintenanceSchema.Validate(contradictory));
    }

    [Fact]
    public void Retention_schema_rejects_selected_operation_in_authoritative_linked_set()
    {
        var selectedOperationId = Guid.NewGuid();
        var selectedCatalog = LinkedCatalogEntry(selectedOperationId) with
        {
            State = OperationState.CanceledNoChanges,
            IsTerminal = true,
            IsRecoveryProtected = false,
        };
        var transactionId = Guid.NewGuid();
        var intent = RetentionIntentDocument.Create(
            transactionId,
            Now,
            new TestRetentionLease(
                transactionId,
                epoch: 1,
                isRecoveryTakeover: false),
            new ActionJournalRetentionRequest(
                selectedOperationId,
                new HashSet<Guid>(),
                TimeSpan.FromDays(365),
                25_000),
            new RetentionArtifactSelection(
                new RetentionOperationIdentity(
                    selectedCatalog.OperationId,
                    selectedCatalog.Revision,
                    selectedCatalog.State,
                    selectedCatalog.LastEventHash,
                    selectedCatalog.TerminalOccurredAtUtc,
                    selectedCatalog.PlanDigest,
                    selectedCatalog.BackupId,
                    selectedCatalog.BackupDigest,
                    selectedCatalog.RootVolumeSerialNumber,
                    selectedCatalog.RootFileIndex,
                    selectedCatalog.IsTerminal,
                    selectedCatalog.IsRecoveryProtected),
                Backup: null,
                ActionEvents: [],
                RetentionLinkedStateSnapshot.Create(
                    [selectedCatalog],
                    selectedOperationId)));
        var contradictory = intent with
        {
            LinkedChangeOperationIds = Array.AsReadOnly(new[] { selectedOperationId }),
        };

        var exception = Assert.Throws<InvalidDataException>(() =>
            RetentionMaintenanceSchema.Validate(contradictory));
        Assert.Equal(
            "The authoritative linked-state operation identifiers are invalid.",
            exception.Message);
    }

    [Theory]
    [InlineData((int)RetentionLifecycleState.DeletingOperation)]
    [InlineData((int)RetentionLifecycleState.DeletingBackup)]
    [InlineData((int)RetentionLifecycleState.DeletingActionEvents)]
    public void Retention_schema_rejects_unreachable_deleting_state_without_artifact(
        int stateValue)
    {
        var transactionId = Guid.NewGuid();
        var lease = new TestRetentionLease(
            transactionId,
            epoch: 1,
            isRecoveryTakeover: false);
        var intent = RetentionIntentDocument.Create(
            transactionId,
            Now,
            lease,
            new ActionJournalRetentionRequest(
                completedOperationId: null,
                new HashSet<Guid>(),
                TimeSpan.FromDays(365),
                25_000),
            RetentionArtifactSelection.Empty);
        var state = new RetentionStateDocument(
            RetentionStateDocument.CurrentSchemaVersion,
            transactionId,
            (RetentionLifecycleState)stateValue,
            Revision: 1,
            lease.LeaseId,
            lease.Epoch,
            Now);

        Assert.Throws<InvalidDataException>(() =>
            RetentionMaintenanceSchema.Validate(state, intent));
    }

    [Fact]
    public void Retention_request_rejects_non_default_maximum_age()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ActionJournalRetentionRequest(
                completedOperationId: null,
                new HashSet<Guid>(),
                TimeSpan.FromDays(364),
                25_000));

        Assert.Equal("maximumAge", exception.ParamName);
    }

    [Fact]
    public void Retention_request_rejects_non_default_maximum_event_count()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ActionJournalRetentionRequest(
                completedOperationId: null,
                new HashSet<Guid>(),
                TimeSpan.FromDays(365),
                24_999));

        Assert.Equal("maximumEventCount", exception.ParamName);
    }

    [Fact]
    public async Task Production_store_enforces_count_limit_with_handle_bound_pruning_and_rebuilds_index()
    {
        using var fixture = ProductionFixture.Create(Now.AddDays(-400));
        for (var index = 0; index < 4; index++)
        {
            await fixture.Journal.AppendAsync(
                Draft(Guid.NewGuid()),
                TestContext.Current.CancellationToken);
            fixture.Clock.Advance(TimeSpan.FromHours(1));
        }

        fixture.Clock.Advance(TimeSpan.FromDays(398));
        for (var index = 0; index < 2; index++)
        {
            await fixture.Journal.AppendAsync(
                Draft(Guid.NewGuid()),
                TestContext.Current.CancellationToken);
            fixture.Clock.Advance(TimeSpan.FromHours(1));
        }

        var transactionId = Guid.NewGuid();
        var coordinator = new ActionJournalRetentionCoordinator(
            fixture.Journal,
            fixture.Lifecycle,
            fixture.Store,
            fixture.Clock);
        var result = await coordinator.RunAsync(
            new AlwaysCurrentLease(transactionId),
            new ActionJournalRetentionRequest(
                null,
                new HashSet<Guid>(),
                TimeSpan.FromDays(365),
                maximumEventCount: 25_000),
            TestContext.Current.CancellationToken);

        Assert.Equal(RetentionLifecycleState.Completed, result.State);
        Assert.Equal(4, result.PlannedActionEventDeletes);
        var remaining = await fixture.Journal.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, remaining.Count);
        Assert.Contains(remaining, item => item.Status == ActionJournalStatus.RetentionApproved);
        Assert.Contains(remaining, item => item.Status == ActionJournalStatus.RetentionCompleted);
        Assert.Equal(4, Directory.EnumerateFiles(
            fixture.Paths.JournalEventsDirectory,
            "*.json",
            SearchOption.TopDirectoryOnly).Count());
        Assert.True(File.Exists(fixture.Paths.JournalIndexFile));
    }

    [Fact]
    public async Task Exact_action_event_identity_swap_after_intent_fails_closed()
    {
        using var fixture = ProductionFixture.Create(Now.AddDays(-400));
        await fixture.Journal.AppendAsync(
            Draft(Guid.NewGuid()),
            TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromDays(400));
        var request = new ActionJournalRetentionRequest(
            null,
            new HashSet<Guid>(),
            TimeSpan.FromDays(365),
            maximumEventCount: 25_000);
        var selection = await fixture.Store.CaptureAsync(
            request,
            fixture.Clock.GetUtcNow(),
            ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
            TestContext.Current.CancellationToken);
        var lease = new AlwaysCurrentLease(Guid.NewGuid());
        var boundary = await PrepareActionDeletionAsync(
            fixture.Lifecycle,
            lease,
            request,
            selection);
        var selected = Assert.Single(selection.ActionEvents);
        var eventPath = fixture.Paths.GetJournalEventFile(selected.EventId);
        var heldPath = Path.Combine(fixture.Root, "held-event.json");
        File.Move(eventPath, heldPath);
        File.WriteAllBytes(eventPath, File.ReadAllBytes(heldPath));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Store.DeleteActionEventsAsync(
                boundary,
                lease,
                TestContext.Current.CancellationToken));

        Assert.True(File.Exists(eventPath));
    }

    [Fact]
    public async Task Already_absent_action_event_is_idempotent_only_with_verified_deleting_intent()
    {
        using var fixture = ProductionFixture.Create(Now.AddDays(-400));
        await fixture.Journal.AppendAsync(
            Draft(Guid.NewGuid()),
            TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromDays(400));
        var request = new ActionJournalRetentionRequest(
            null,
            new HashSet<Guid>(),
            TimeSpan.FromDays(365),
            maximumEventCount: 25_000);
        var selection = await fixture.Store.CaptureAsync(
            request,
            fixture.Clock.GetUtcNow(),
            ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
            TestContext.Current.CancellationToken);
        var lease = new AlwaysCurrentLease(Guid.NewGuid());
        var boundary = await PrepareActionDeletionAsync(
            fixture.Lifecycle,
            lease,
            request,
            selection);
        File.Delete(fixture.Paths.GetJournalEventFile(Assert.Single(selection.ActionEvents).EventId));

        var deleted = await fixture.Store.DeleteActionEventsAsync(
            boundary,
            lease,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, deleted);
        var wrongState = boundary with { State = RetentionLifecycleState.BackupDeleted };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Store.DeleteActionEventsAsync(
                wrongState,
                lease,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Operation_and_backup_must_form_one_verified_exact_binding_before_intent()
    {
        using var fixture = ProductionFixture.Create(Now);
        var operationId = Guid.NewGuid();
        var backupId = Guid.NewGuid().ToString("N");
        var operation = new OperationStorageCatalogEntry(
            operationId,
            Revision: 4,
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
        var backup = new BackupStorageCatalogEntry(
            backupId,
            BackupStorageStatus.VerifiedCommitted,
            BackupCaptureKind.Operation,
            PlanDigest: new string('D', 64),
            BackupDigest: operation.BackupDigest,
            BackupProtectionClass.OperationRollbackSource,
            IsVerified: true,
            IsRecoveryProtected: true,
            CommittedUtc: Now.AddDays(-100));
        var store = new WinoraRetentionArtifactStore(
            new StubOperationRetentionStore([operation]),
            new StubBackupRetentionStore([backup]),
            fixture.Journal);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.CaptureAsync(
                new ActionJournalRetentionRequest(
                    operationId,
                    new HashSet<Guid>(),
                    TimeSpan.FromDays(365),
                    25_000),
                Now,
                ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, false)]
    [InlineData(51, true)]
    public async Task Backup_retention_keeps_newest_fifty_verified_operation_backups(
        int backupCount,
        bool expectedEligible)
    {
        using var fixture = ProductionFixture.Create(Now);
        var (operation, backups) = CatalogWithTargetAsOldest(
            backupCount,
            Now.AddDays(-100),
            Now.AddDays(-100));
        var store = new WinoraRetentionArtifactStore(
            new StubOperationRetentionStore([operation]),
            new StubBackupRetentionStore(backups),
            fixture.Journal);
        var capture = () => store.CaptureAsync(
            RequestFor(operation.OperationId),
            Now,
            ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
            TestContext.Current.CancellationToken).AsTask();

        if (!expectedEligible)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(capture);
            return;
        }

        var selection = await capture();
        Assert.Equal(operation.BackupId, selection.Backup?.BackupId);
    }

    [Theory]
    [InlineData(89, false)]
    [InlineData(90, true)]
    [InlineData(91, true)]
    public async Task Operation_and_linked_backup_require_at_least_ninety_days(
        int ageDays,
        bool expectedEligible)
    {
        using var fixture = ProductionFixture.Create(Now);
        var occurredUtc = Now.AddDays(-ageDays);
        var (operation, backups) = CatalogWithTargetAsOldest(
            51,
            occurredUtc,
            occurredUtc);
        var store = new WinoraRetentionArtifactStore(
            new StubOperationRetentionStore([operation]),
            new StubBackupRetentionStore(backups),
            fixture.Journal);
        var capture = () => store.CaptureAsync(
            RequestFor(operation.OperationId),
            Now,
            ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
            TestContext.Current.CancellationToken).AsTask();

        if (!expectedEligible)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(capture);
        }
        else
        {
            Assert.NotNull((await capture()).Backup);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Missing_or_noncanonical_authoritative_timestamp_fails_closed(int defect)
    {
        using var fixture = ProductionFixture.Create(Now);
        var (operation, backups) = CatalogWithTargetAsOldest(
            51,
            Now.AddDays(-100),
            Now.AddDays(-100));
        operation = defect switch
        {
            0 => operation with { TerminalOccurredAtUtc = default },
            1 => operation with
            {
                TerminalOccurredAtUtc = operation.TerminalOccurredAtUtc.ToOffset(
                    TimeSpan.FromHours(3)),
            },
            _ => operation,
        };
        if (defect == 2)
        {
            backups = backups.Select((item, index) =>
                    index == 0 ? item with { CommittedUtc = null } : item)
                .ToArray();
        }

        var store = new WinoraRetentionArtifactStore(
            new StubOperationRetentionStore([operation]),
            new StubBackupRetentionStore(backups),
            fixture.Journal);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.CaptureAsync(
                RequestFor(operation.OperationId),
                Now,
                ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(89, 100)]
    [InlineData(100, 89)]
    public async Task Both_operation_and_backup_must_cross_the_ninety_day_boundary(
        int operationAgeDays,
        int backupAgeDays)
    {
        using var fixture = ProductionFixture.Create(Now);
        var (operation, backups) = CatalogWithTargetAsOldest(
            51,
            Now.AddDays(-operationAgeDays),
            Now.AddDays(-backupAgeDays));
        var store = new WinoraRetentionArtifactStore(
            new StubOperationRetentionStore([operation]),
            new StubBackupRetentionStore(backups),
            fixture.Journal);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CaptureAsync(
                RequestFor(operation.OperationId),
                Now,
                ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Linked_completed_change_and_its_rollback_backup_are_never_selected()
    {
        using var fixture = ProductionFixture.Create(Now);
        var (operation, backups) = CatalogWithTargetAsOldest(
            51,
            Now.AddDays(-100),
            Now.AddDays(-100));
        var store = new WinoraRetentionArtifactStore(
            new StubOperationRetentionStore([operation]),
            new StubBackupRetentionStore(backups),
            fixture.Journal);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CaptureAsync(
                RequestFor(operation.OperationId, operation.OperationId),
                Now,
                ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Backup_referenced_by_an_active_operation_is_never_selected()
    {
        using var fixture = ProductionFixture.Create(Now);
        var (operation, backups) = CatalogWithTargetAsOldest(
            51,
            Now.AddDays(-100),
            Now.AddDays(-100));
        var active = operation with
        {
            OperationId = Guid.NewGuid(),
            Revision = 2,
            State = OperationState.Applying,
            TerminalOccurredAtUtc = Now.AddDays(-1),
            IsTerminal = false,
            IsRecoveryProtected = true,
        };
        var store = new WinoraRetentionArtifactStore(
            new StubOperationRetentionStore([operation, active]),
            new StubBackupRetentionStore(backups),
            fixture.Journal);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CaptureAsync(
                RequestFor(operation.OperationId),
                Now,
                ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Each_invalid_linked_backup_field_fails_closed_with_invalid_data(int defect)
    {
        using var fixture = ProductionFixture.Create(Now);
        var (operation, backups) = CatalogWithTargetAsOldest(
            51,
            Now.AddDays(-100),
            Now.AddDays(-100));
        backups[0] = defect switch
        {
            0 => backups[0] with { Status = BackupStorageStatus.UnmarkedOrCorruptFinal },
            1 => backups[0] with { Kind = null },
            2 => backups[0] with { PlanDigest = null },
            3 => backups[0] with { BackupDigest = null },
            4 => backups[0] with { Protection = BackupProtectionClass.RecoveryRequired },
            5 => backups[0] with { IsVerified = false },
            _ => backups[0] with { CommittedUtc = null },
        };
        var store = new WinoraRetentionArtifactStore(
            new StubOperationRetentionStore([operation]),
            new StubBackupRetentionStore(backups),
            fixture.Journal);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.CaptureAsync(
                RequestFor(operation.OperationId),
                Now,
                ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_timestamp_on_any_verified_operation_backup_fails_closed()
    {
        using var fixture = ProductionFixture.Create(Now);
        var (operation, backups) = CatalogWithTargetAsOldest(
            51,
            Now.AddDays(-100),
            Now.AddDays(-100));
        backups[^1] = backups[^1] with { CommittedUtc = null };
        var store = new WinoraRetentionArtifactStore(
            new StubOperationRetentionStore([operation]),
            new StubBackupRetentionStore(backups),
            fixture.Journal);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.CaptureAsync(
                RequestFor(operation.OperationId),
                Now,
                ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    public async Task Equal_backup_timestamps_use_canonical_backup_id_as_the_tie_break(
        int targetIndex,
        bool expectedEligible)
    {
        using var fixture = ProductionFixture.Create(Now);
        var (operation, backups) = CatalogWithEqualBackupTimestamps(targetIndex);
        var store = new WinoraRetentionArtifactStore(
            new StubOperationRetentionStore([operation]),
            new StubBackupRetentionStore(backups),
            fixture.Journal);
        var capture = () => store.CaptureAsync(
            RequestFor(operation.OperationId),
            Now,
            ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
            TestContext.Current.CancellationToken).AsTask();

        if (expectedEligible)
        {
            Assert.Equal(operation.BackupId, (await capture()).Backup?.BackupId);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(capture);
        }
    }

    [Fact]
    public async Task Recovery_protected_operation_is_never_selected_even_when_old()
    {
        using var fixture = ProductionFixture.Create(Now);
        var (operation, backups) = CatalogWithTargetAsOldest(
            51,
            Now.AddDays(-100),
            Now.AddDays(-100));
        operation = operation with { IsRecoveryProtected = true };
        var store = new WinoraRetentionArtifactStore(
            new StubOperationRetentionStore([operation]),
            new StubBackupRetentionStore(backups),
            fixture.Journal);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CaptureAsync(
                RequestFor(operation.OperationId),
                Now,
                ActionJournalRetentionCoordinator.ReservedDecisionEventCount,
                TestContext.Current.CancellationToken));
    }

    private static ActionJournalRetentionRequest RequestFor(
        Guid operationId,
        params Guid[] linkedOperationIds) =>
        new(
            operationId,
            new HashSet<Guid>(linkedOperationIds),
            TimeSpan.FromDays(365),
            25_000);

    private static OperationStorageCatalogEntry LinkedCatalogEntry(Guid operationId) =>
        new(
            operationId,
            Revision: 3,
            OperationState.Applying,
            LastEventHash: new string('A', 64),
            TerminalOccurredAtUtc: Now.AddDays(-10),
            PlanDigest: new string('B', 64),
            BackupId: null,
            BackupDigest: null,
            RootVolumeSerialNumber: 1,
            RootFileIndex: 1,
            IsTerminal: false,
            IsRecoveryProtected: true);

    private static (OperationStorageCatalogEntry Operation, BackupStorageCatalogEntry[] Backups)
        CatalogWithEqualBackupTimestamps(int targetIndex)
    {
        const int backupCount = 51;
        var backupIds = Enumerable.Range(1, backupCount)
            .Select(index => Guid.Parse(index.ToString("x32")).ToString("N"))
            .ToArray();
        var planDigest = new string('B', 64);
        var backupDigest = new string('C', 64);
        var operation = new OperationStorageCatalogEntry(
            Guid.NewGuid(),
            Revision: 7,
            OperationState.Completed,
            LastEventHash: new string('A', 64),
            TerminalOccurredAtUtc: Now.AddDays(-100),
            planDigest,
            BackupId: backupIds[targetIndex],
            BackupDigest: backupDigest,
            RootVolumeSerialNumber: 1,
            RootFileIndex: 1,
            IsTerminal: true,
            IsRecoveryProtected: false);
        var backups = backupIds.Select((backupId, index) => new BackupStorageCatalogEntry(
                backupId,
                BackupStorageStatus.VerifiedCommitted,
                BackupCaptureKind.Operation,
                index == targetIndex ? planDigest : new string('E', 64),
                index == targetIndex ? backupDigest : new string('F', 64),
                BackupProtectionClass.OperationRollbackSource,
                IsVerified: true,
                IsRecoveryProtected: true,
                CommittedUtc: Now.AddDays(-100)))
            .ToArray();
        return (operation, backups);
    }

    private static (OperationStorageCatalogEntry Operation, BackupStorageCatalogEntry[] Backups)
        CatalogWithTargetAsOldest(
            int backupCount,
            DateTimeOffset terminalOccurredAtUtc,
            DateTimeOffset committedUtc)
    {
        var operationId = Guid.NewGuid();
        var targetBackupId = Guid.NewGuid().ToString("N");
        var planDigest = new string('B', 64);
        var backupDigest = new string('C', 64);
        var operation = new OperationStorageCatalogEntry(
            operationId,
            Revision: 7,
            OperationState.Completed,
            LastEventHash: new string('A', 64),
            terminalOccurredAtUtc,
            planDigest,
            targetBackupId,
            backupDigest,
            RootVolumeSerialNumber: 1,
            RootFileIndex: 1,
            IsTerminal: true,
            IsRecoveryProtected: false);
        var backups = Enumerable.Range(0, backupCount)
            .Select(index => new BackupStorageCatalogEntry(
                index == 0 ? targetBackupId : Guid.NewGuid().ToString("N"),
                BackupStorageStatus.VerifiedCommitted,
                BackupCaptureKind.Operation,
                index == 0 ? planDigest : new string('E', 64),
                index == 0 ? backupDigest : new string('F', 64),
                BackupProtectionClass.OperationRollbackSource,
                IsVerified: true,
                IsRecoveryProtected: true,
                CommittedUtc: committedUtc.AddMinutes(index)))
            .ToArray();
        return (operation, backups);
    }

    private static async ValueTask<RetentionTransactionBoundary> PrepareActionDeletionAsync(
        DurableRetentionJournal lifecycle,
        IMutationLeaseHandle lease,
        ActionJournalRetentionRequest request,
        RetentionArtifactSelection selection)
    {
        var boundary = await lifecycle.CreateApprovedAsync(
            lease.OperationId,
            lease,
            request,
            selection,
            TestContext.Current.CancellationToken);
        boundary = await lifecycle.AdvanceAsync(
            boundary,
            RetentionLifecycleState.OperationDeleted,
            lease,
            TestContext.Current.CancellationToken);
        boundary = await lifecycle.AdvanceAsync(
            boundary,
            RetentionLifecycleState.BackupDeleted,
            lease,
            TestContext.Current.CancellationToken);
        return await lifecycle.AdvanceAsync(
            boundary,
            RetentionLifecycleState.DeletingActionEvents,
            lease,
            TestContext.Current.CancellationToken);
    }

    private static ActionJournalEntryDraft Draft(Guid operationId) =>
        new(
            operationId,
            "windows.effects.transparency",
            ActionJournalEventKind.Operation,
            ActionJournalCategory.WindowsPersonalization,
            ActionJournalStatus.Succeeded,
            ActionJournalRisk.Low,
            ActionJournalPrivilege.StandardUser,
            ActionJournalSupportStatus.Supported,
            Guid.NewGuid(),
            null,
            1);

    private static ActionJournalEntryDraft RollbackFailedDraft(Guid operationId) =>
        new(
            operationId,
            "windows.effects.transparency",
            ActionJournalEventKind.Operation,
            ActionJournalCategory.WindowsPersonalization,
            ActionJournalStatus.RollbackFailed,
            ActionJournalRisk.Low,
            ActionJournalPrivilege.StandardUser,
            ActionJournalSupportStatus.Supported,
            Guid.NewGuid(),
            null,
            1);

    private static async ValueTask AppendTransitionAsync(
        DurableOperationJournal journal,
        OperationTransition transition)
    {
        var result = await journal.CompareAndAppendAsync(
            transition,
            TestContext.Current.CancellationToken);
        Assert.True(result.IsDurable);
    }

    private sealed class ProductionFixture : IDisposable
    {
        private ProductionFixture(
            string root,
            WinoraDataPaths paths,
            MutableJournalTimeProvider clock,
            ActionJournal journal,
            DurableOperationJournal operations,
            DurableRetentionJournal lifecycle,
            WinoraRetentionArtifactStore store)
        {
            Root = root;
            Paths = paths;
            Clock = clock;
            Journal = journal;
            Operations = operations;
            Lifecycle = lifecycle;
            Store = store;
        }

        internal string Root { get; }
        internal WinoraDataPaths Paths { get; }
        internal MutableJournalTimeProvider Clock { get; }
        internal ActionJournal Journal { get; }
        internal DurableOperationJournal Operations { get; }
        internal DurableRetentionJournal Lifecycle { get; }
        internal WinoraRetentionArtifactStore Store { get; }

        internal static ProductionFixture Create(DateTimeOffset now)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "Winora.Tests",
                "ProductionRetention",
                Guid.NewGuid().ToString("N"));
            var paths = new WinoraDataPaths(root);
            var clock = new MutableJournalTimeProvider(now);
            var journal = new ActionJournal(
                paths,
                new FixedActionJournalOperationCatalog(
                    ["windows.effects.transparency", "winora.retention"]),
                clock);
            var operations = new DurableOperationJournal(paths, DurableJournalActor.App, clock);
            var backups = new BackupRepository(paths, new NeverCaptureBackupProvider(), clock);
            return new ProductionFixture(
                root,
                paths,
                clock,
                journal,
                operations,
                new DurableRetentionJournal(paths, clock),
                new WinoraRetentionArtifactStore(operations, backups, journal));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class AlwaysCurrentLease(Guid operationId) : IMutationLeaseHandle
    {
        public Guid LeaseId { get; } = Guid.NewGuid();
        public Guid OperationId { get; } = operationId;
        public long Epoch => 1;
        public bool IsRecoveryTakeover => false;
        public ValueTask<bool> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
        public ValueTask<bool> HeartbeatAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestRetentionLease(
        Guid operationId,
        long epoch,
        bool isRecoveryTakeover) : IMutationLeaseHandle
    {
        public Guid LeaseId { get; } = Guid.NewGuid();
        public Guid OperationId { get; } = operationId;
        public long Epoch { get; } = epoch;
        public bool IsRecoveryTakeover { get; } = isRecoveryTakeover;
        public ValueTask<bool> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
        public ValueTask<bool> HeartbeatAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SequenceRetentionLease(
        Guid operationId,
        IReadOnlyList<bool> revalidationResults) : IMutationLeaseHandle
    {
        private int _revalidationCount;

        public Guid LeaseId { get; } = Guid.NewGuid();
        public Guid OperationId { get; } = operationId;
        public long Epoch => 1;
        public bool IsRecoveryTakeover => false;
        internal int RevalidationCount => Volatile.Read(ref _revalidationCount);

        public ValueTask<bool> RevalidateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _revalidationCount) - 1;
            return ValueTask.FromResult(
                index < revalidationResults.Count && revalidationResults[index]);
        }

        public ValueTask<bool> HeartbeatAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NeverCaptureBackupProvider : IBackupCaptureProvider
    {
        public ValueTask<BackupCapture> CaptureOperationAsync(
            ChangePlan plan,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException();

        public ValueTask<BackupCapture> CaptureRecoveryCheckpointAsync(
            RollbackPlan plan,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }

    private sealed class StubOperationRetentionStore(
        IReadOnlyList<OperationStorageCatalogEntry> catalog) : IOperationRetentionStore
    {
        public ValueTask<IReadOnlyList<OperationStorageCatalogEntry>> ScanAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult(catalog);

        public ValueTask<bool> DeleteAsync(
            OperationStorageCatalogEntry expected,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class SequenceOperationRetentionStore(
        params IReadOnlyList<OperationStorageCatalogEntry>[] catalogs) : IOperationRetentionStore
    {
        private int _scanCount;

        internal int ScanCount => Volatile.Read(ref _scanCount);

        public ValueTask<IReadOnlyList<OperationStorageCatalogEntry>> ScanAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _scanCount) - 1;
            return ValueTask.FromResult(
                catalogs[Math.Min(index, catalogs.Length - 1)]);
        }

        public ValueTask<bool> DeleteAsync(
            OperationStorageCatalogEntry expected,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class MutableOperationRetentionStore : IOperationRetentionStore
    {
        internal IReadOnlyList<OperationStorageCatalogEntry> Catalog { get; set; } = [];

        public ValueTask<IReadOnlyList<OperationStorageCatalogEntry>> ScanAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Catalog);
        }

        public ValueTask<bool> DeleteAsync(
            OperationStorageCatalogEntry expected,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class StubBackupRetentionStore(
        IReadOnlyList<BackupStorageCatalogEntry> catalog) : IBackupRetentionStore
    {
        public ValueTask<IReadOnlyList<BackupStorageCatalogEntry>> ScanAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult(catalog);

        public ValueTask<bool> DeleteAsync(
            BackupStorageCatalogEntry expected,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }
}
