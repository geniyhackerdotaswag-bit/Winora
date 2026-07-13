using Winora.Core.Changes;
using Winora.Core.Contracts;
using Xunit;

namespace Winora.Core.Tests.Changes;

public sealed class ChangePlanTests
{
    [Fact]
    public void Equivalent_plans_have_the_same_digest()
    {
        Assert.Equal(PlanFixture.Create().Digest, PlanFixture.Create().Digest);
    }

    [Fact]
    public void Digest_changes_when_canonical_content_changes()
    {
        var baseline = PlanFixture.Create();
        var changed = PlanFixture.Create(title: "Different title");

        Assert.NotEqual(baseline.Digest, changed.Digest);
    }

    [Fact]
    public void Digest_cannot_be_supplied_by_a_caller()
    {
        Assert.Empty(typeof(ChangePlan).GetConstructors());
    }

    [Fact]
    public void Durable_operation_facts_cannot_be_supplied_by_a_caller()
    {
        Assert.Empty(typeof(Winora.Core.Contracts.DurableOperationFacts).GetConstructors());
    }

    [Fact]
    public void Restore_lifecycle_transition_requires_typed_facts()
    {
        var plan = PlanFixture.Create();
        var facts = Winora.Core.Contracts.DurableOperationFacts.From(plan);

        Assert.Throws<ArgumentException>(() =>
            Winora.Core.Contracts.OperationTransition.Create(
                plan.PlanId,
                facts,
                expectedRevision: 2,
                OperationState.Prepared,
                OperationState.RestorePointBeginRequested,
                stepId: null,
                new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
                previousFacts: facts));
    }

    [Fact]
    public void Durable_transition_cannot_be_supplied_without_validation()
    {
        Assert.Empty(typeof(OperationTransition).GetConstructors());
    }

    [Theory]
    [InlineData(typeof(DurableOperationFacts))]
    [InlineData(typeof(RestorePointTransitionFacts))]
    [InlineData(typeof(OperationTransition))]
    [InlineData(typeof(DurableOperationBoundary))]
    [InlineData(typeof(DurableTransitionResult))]
    public void Durable_proof_types_do_not_expose_public_issuance_factories(Type proofType)
    {
        Assert.DoesNotContain(
            proofType.GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Instance),
            method =>
                method.DeclaringType == proofType &&
                method.ReturnType == proofType &&
                !method.Name.Contains("Clone", StringComparison.Ordinal));
    }

    [Fact]
    public void Restore_point_begun_requires_verified_ownership_and_inventory_evidence()
    {
        var plan = PlanFixture.Create(
            risk: RiskLevel.Informational,
            rollback: RollbackCapability.NotApplicable,
            backup: BackupRequirement.NotApplicable,
            kind: ChangePlanKind.ManualRestorePointArtifact);
        var facts = DurableOperationFacts.From(plan);
        var correlation = Guid.Parse("d572f642-1e9b-4e77-a053-1aa781e4dcb9");
        var returnedAt = new DateTimeOffset(2026, 7, 13, 0, 1, 0, TimeSpan.Zero);
        var returned = RestorePointTransitionFacts.Create(
            correlationId: correlation,
            description: "Winora restore point",
            preBeginInventoryFingerprint: PlanFixture.Fingerprint("inventory-before"),
            postBeginInventoryFingerprint: null,
            sequenceNumber: 42,
            beginApiStatus: RestorePointApiStatus.Succeeded,
            ownershipStatus: RestorePointOwnershipStatus.NotChecked,
            finalizationMode: RestorePointFinalizationMode.None,
            beginReturnedAtUtc: returnedAt,
            finalizationRequestedAtUtc: null,
            finalizationApiStatus: RestorePointApiStatus.NotCalled);
        var unprovenBegun = RestorePointTransitionFacts.Create(
            correlationId: correlation,
            description: "Winora restore point",
            preBeginInventoryFingerprint: PlanFixture.Fingerprint("inventory-before"),
            postBeginInventoryFingerprint: null,
            sequenceNumber: 42,
            beginApiStatus: RestorePointApiStatus.Succeeded,
            ownershipStatus: RestorePointOwnershipStatus.NotChecked,
            finalizationMode: RestorePointFinalizationMode.None,
            beginReturnedAtUtc: returnedAt,
            finalizationRequestedAtUtc: null,
            finalizationApiStatus: RestorePointApiStatus.NotCalled);

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                facts,
                expectedRevision: 4,
                OperationState.RestorePointBeginReturnedUnverified,
                OperationState.RestorePointBegun,
                stepId: null,
                returnedAt.AddSeconds(1),
                unprovenBegun,
                returned,
                facts));
    }

    [Fact]
    public void Target_mutation_cannot_begin_restore_before_verified_backup()
    {
        var plan = PlanFixture.Create(risk: RiskLevel.High, requiresRestorePoint: true);
        var facts = DurableOperationFacts.From(plan);
        var restoreFacts = RestorePointTransitionFacts.Create(
            correlationId: Guid.Parse("d572f642-1e9b-4e77-a053-1aa781e4dcb9"),
            description: "Winora restore point",
            preBeginInventoryFingerprint: PlanFixture.Fingerprint("inventory-before"),
            postBeginInventoryFingerprint: null,
            sequenceNumber: null,
            beginApiStatus: RestorePointApiStatus.NotCalled,
            ownershipStatus: RestorePointOwnershipStatus.NotChecked,
            finalizationMode: RestorePointFinalizationMode.None,
            beginReturnedAtUtc: null,
            finalizationRequestedAtUtc: null,
            finalizationApiStatus: RestorePointApiStatus.NotCalled);

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                facts,
                expectedRevision: 2,
                OperationState.Prepared,
                OperationState.RestorePointBeginRequested,
                stepId: null,
                new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
                restoreFacts,
                previousFacts: facts));
    }

    [Fact]
    public void Restore_lifecycle_cannot_change_correlation_between_events()
    {
        var plan = PlanFixture.Create(
            risk: RiskLevel.Informational,
            rollback: RollbackCapability.NotApplicable,
            backup: BackupRequirement.NotApplicable,
            kind: ChangePlanKind.ManualRestorePointArtifact);
        var facts = DurableOperationFacts.From(plan);
        var returnedAt = new DateTimeOffset(2026, 7, 13, 0, 1, 0, TimeSpan.Zero);
        var previous = RestorePointTransitionFacts.Create(
            Guid.Parse("d572f642-1e9b-4e77-a053-1aa781e4dcb9"),
            "Winora restore point",
            PlanFixture.Fingerprint("inventory-before"),
            null,
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.NotChecked,
            RestorePointFinalizationMode.None,
            returnedAt,
            null,
            RestorePointApiStatus.NotCalled);
        var changedCorrelation = RestorePointTransitionFacts.Create(
            Guid.Parse("8ecb89dc-323c-43fc-9f19-93e0609a4fcf"),
            "Winora restore point",
            PlanFixture.Fingerprint("inventory-before"),
            PlanFixture.Fingerprint("inventory-after"),
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.VerifiedNewAndOwned,
            RestorePointFinalizationMode.None,
            returnedAt,
            null,
            RestorePointApiStatus.NotCalled);

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                facts,
                expectedRevision: 4,
                OperationState.RestorePointBeginReturnedUnverified,
                OperationState.RestorePointBegun,
                stepId: null,
                returnedAt.AddSeconds(1),
                changedCorrelation,
                previous,
                facts));
    }

    [Fact]
    public void Restore_boundary_preserves_verified_lifecycle_facts()
    {
        var plan = PlanFixture.Create(
            risk: RiskLevel.Informational,
            rollback: RollbackCapability.NotApplicable,
            backup: BackupRequirement.NotApplicable,
            kind: ChangePlanKind.ManualRestorePointArtifact);
        var restoreFacts = RestorePointTransitionFacts.Create(
            Guid.Parse("d572f642-1e9b-4e77-a053-1aa781e4dcb9"),
            "Winora restore point",
            PlanFixture.Fingerprint("inventory-before"),
            PlanFixture.Fingerprint("inventory-after"),
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.VerifiedNewAndOwned,
            RestorePointFinalizationMode.None,
            new DateTimeOffset(2026, 7, 13, 0, 1, 0, TimeSpan.Zero),
            null,
            RestorePointApiStatus.NotCalled);

        var boundary = DurableOperationBoundary.Create(
            plan.PlanId,
            DurableOperationFacts.From(plan),
            revision: 5,
            OperationState.RestorePointBegun,
            stepId: null,
            appliedStepIds: [],
            restorePoint: restoreFacts);

        Assert.Equal(restoreFacts.Digest, boundary.RestorePoint?.Digest);
    }

    [Fact]
    public void Known_finalization_failure_allows_a_reviewed_retry_with_the_same_restore_identity()
    {
        var plan = PlanFixture.Create(
            risk: RiskLevel.Informational,
            rollback: RollbackCapability.NotApplicable,
            backup: BackupRequirement.NotApplicable,
            kind: ChangePlanKind.ManualRestorePointArtifact);
        var correlation = Guid.Parse("d572f642-1e9b-4e77-a053-1aa781e4dcb9");
        var beginReturnedAt = new DateTimeOffset(2026, 7, 13, 0, 1, 0, TimeSpan.Zero);
        var failedAt = beginReturnedAt.AddMinutes(1);
        var facts = DurableOperationFacts.From(plan);
        var failed = RestorePointTransitionFacts.Create(
            correlation,
            "Winora restore point",
            PlanFixture.Fingerprint("inventory-before"),
            PlanFixture.Fingerprint("inventory-after"),
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.VerifiedNewAndOwned,
            RestorePointFinalizationMode.Normal,
            beginReturnedAt,
            failedAt,
            RestorePointApiStatus.KnownFailure);
        var retry = RestorePointTransitionFacts.Create(
            correlation,
            "Winora restore point",
            PlanFixture.Fingerprint("inventory-before"),
            PlanFixture.Fingerprint("inventory-after"),
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.VerifiedNewAndOwned,
            RestorePointFinalizationMode.Normal,
            beginReturnedAt,
            failedAt.AddMinutes(1),
            RestorePointApiStatus.NotCalled);

        var transition = OperationTransition.Create(
            plan.PlanId,
            facts,
            expectedRevision: 7,
            OperationState.RestorePointFinalizeFailedRecoveryRequired,
            OperationState.RestorePointEndRequested,
            stepId: null,
            failedAt.AddMinutes(1),
            retry,
            failed,
            facts);

        Assert.Equal(failed.Digest, transition.ExpectedRestorePointFactsDigest);
    }

    [Fact]
    public void Known_cancel_failure_allows_only_a_reviewed_cancel_retry()
    {
        var plan = PlanFixture.Create(
            risk: RiskLevel.Informational,
            rollback: RollbackCapability.NotApplicable,
            backup: BackupRequirement.NotApplicable,
            kind: ChangePlanKind.ManualRestorePointArtifact);
        var facts = DurableOperationFacts.From(plan);
        var beginReturnedAt = new DateTimeOffset(2026, 7, 13, 0, 1, 0, TimeSpan.Zero);
        var failedAt = beginReturnedAt.AddMinutes(1);
        var failed = RestorePointTransitionFacts.Create(
            Guid.Parse("d572f642-1e9b-4e77-a053-1aa781e4dcb9"),
            "Winora restore point",
            PlanFixture.Fingerprint("inventory-before"),
            PlanFixture.Fingerprint("inventory-after"),
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.VerifiedNewAndOwned,
            RestorePointFinalizationMode.Cancelled,
            beginReturnedAt,
            failedAt,
            RestorePointApiStatus.KnownFailure);
        var retry = RestorePointTransitionFacts.Create(
            failed.CorrelationId,
            failed.Description,
            failed.PreBeginInventoryFingerprint,
            failed.PostBeginInventoryFingerprint,
            failed.SequenceNumber,
            failed.BeginApiStatus,
            failed.OwnershipStatus,
            RestorePointFinalizationMode.Cancelled,
            failed.BeginReturnedAtUtc,
            failedAt.AddMinutes(1),
            RestorePointApiStatus.NotCalled);

        var transition = OperationTransition.Create(
            plan.PlanId,
            facts,
            expectedRevision: 7,
            OperationState.RestorePointFinalizeFailedRecoveryRequired,
            OperationState.RestorePointCancelRequested,
            stepId: null,
            failedAt.AddMinutes(1),
            retry,
            failed,
            facts);

        Assert.Equal(failed.Digest, transition.ExpectedRestorePointFactsDigest);
    }

    [Fact]
    public void Ambiguous_restore_ownership_cannot_be_terminally_classified_as_no_changes()
    {
        var plan = PlanFixture.Create(
            risk: RiskLevel.Informational,
            rollback: RollbackCapability.NotApplicable,
            backup: BackupRequirement.NotApplicable,
            kind: ChangePlanKind.ManualRestorePointArtifact);
        var correlation = Guid.Parse("d572f642-1e9b-4e77-a053-1aa781e4dcb9");
        var returnedAt = new DateTimeOffset(2026, 7, 13, 0, 1, 0, TimeSpan.Zero);
        var facts = DurableOperationFacts.From(plan);
        var returned = RestorePointTransitionFacts.Create(
            correlation,
            "Winora restore point",
            PlanFixture.Fingerprint("inventory-before"),
            null,
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.NotChecked,
            RestorePointFinalizationMode.None,
            returnedAt,
            null,
            RestorePointApiStatus.NotCalled);
        var ambiguous = RestorePointTransitionFacts.Create(
            correlation,
            "Winora restore point",
            PlanFixture.Fingerprint("inventory-before"),
            PlanFixture.Fingerprint("inventory-after"),
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.Ambiguous,
            RestorePointFinalizationMode.None,
            returnedAt,
            null,
            RestorePointApiStatus.NotCalled);

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                facts,
                expectedRevision: 4,
                OperationState.RestorePointBeginReturnedUnverified,
                OperationState.RestorePointFailedNoChanges,
                stepId: null,
                returnedAt.AddSeconds(1),
                ambiguous,
                returned,
                facts));
    }

    [Fact]
    public void Applying_boundary_requires_the_exact_prior_applied_prefix()
    {
        var plan = PlanFixture.Create(steps: [PlanFixture.Step("first"), PlanFixture.Step("second")]);
        var facts = DurableOperationFacts.From(plan).WithBackupDigest("BACKUP-DIGEST");

        Assert.Throws<ArgumentException>(() =>
            DurableOperationBoundary.Create(
                plan.PlanId,
                facts,
                revision: 4,
                OperationState.Applying,
                stepId: "second",
                appliedStepIds: ["second"]));
    }

    [Fact]
    public void Applying_transition_requires_its_exact_step_identifier()
    {
        var plan = PlanFixture.Create();
        var facts = DurableOperationFacts.From(plan).WithBackupDigest("BACKUP-DIGEST");

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                facts,
                expectedRevision: 3,
                OperationState.BackupCreated,
                OperationState.Applying,
                stepId: null,
                new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
                previousFacts: facts));
    }

    [Fact]
    public void Rolling_back_transition_requires_its_exact_step_identifier()
    {
        var rollback = RollbackFixture.Create(PlanFixture.Create());
        var facts = DurableOperationFacts.From(rollback)
            .WithRecoveryCheckpoint("checkpoint", "CHECKPOINT-DIGEST");

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                rollback.RollbackId,
                facts,
                expectedRevision: 3,
                OperationState.RollbackCheckpointCreated,
                OperationState.RollingBack,
                stepId: null,
                new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
                previousFacts: facts));
    }

    [Fact]
    public void Backup_created_transition_requires_a_verified_backup_digest()
    {
        var plan = PlanFixture.Create();
        var facts = DurableOperationFacts.From(plan);

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                facts,
                expectedRevision: 2,
                OperationState.Prepared,
                OperationState.BackupCreated,
                stepId: null,
                new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
                previousFacts: facts));
    }

    [Fact]
    public void Durable_facts_cannot_replace_an_established_backup_digest()
    {
        var plan = PlanFixture.Create();
        var previousFacts = DurableOperationFacts.From(plan).WithBackupDigest("BACKUP-DIGEST");
        var replacedFacts = previousFacts.WithBackupDigest("REPLACED-DIGEST");

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                replacedFacts,
                expectedRevision: 3,
                OperationState.BackupCreated,
                OperationState.Applying,
                stepId: plan.Steps[0].StepId,
                new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
                previousFacts: previousFacts));
    }

    [Fact]
    public void Planned_transition_rejects_prepopulated_backup_facts()
    {
        var plan = PlanFixture.Create();

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                DurableOperationFacts.From(plan).WithBackupDigest("UNVERIFIED-DIGEST"),
                expectedRevision: 0,
                expectedState: null,
                OperationState.Planned,
                stepId: null,
                new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Rollback_planned_transition_rejects_prepopulated_checkpoint_facts()
    {
        var rollback = RollbackFixture.Create(PlanFixture.Create());

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                rollback.RollbackId,
                DurableOperationFacts.From(rollback)
                    .WithRecoveryCheckpoint("checkpoint", "UNVERIFIED-DIGEST"),
                expectedRevision: 0,
                expectedState: null,
                OperationState.RollbackPlanned,
                stepId: null,
                new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Rollback_checkpoint_transition_requires_verified_checkpoint_facts()
    {
        var rollback = RollbackFixture.Create(PlanFixture.Create());
        var facts = DurableOperationFacts.From(rollback);

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                rollback.RollbackId,
                facts,
                expectedRevision: 2,
                OperationState.RollbackPrepared,
                OperationState.RollbackCheckpointCreated,
                stepId: null,
                new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
                previousFacts: facts));
    }

    [Fact]
    public void Backup_created_boundary_requires_a_verified_backup_digest()
    {
        var plan = PlanFixture.Create();

        Assert.Throws<ArgumentException>(() =>
            DurableOperationBoundary.Create(
                plan.PlanId,
                DurableOperationFacts.From(plan),
                revision: 3,
                OperationState.BackupCreated,
                stepId: null,
                appliedStepIds: []));
    }

    [Fact]
    public void Rollback_checkpoint_boundary_requires_verified_checkpoint_facts()
    {
        var rollback = RollbackFixture.Create(PlanFixture.Create());

        Assert.Throws<ArgumentException>(() =>
            DurableOperationBoundary.Create(
                rollback.RollbackId,
                DurableOperationFacts.From(rollback),
                revision: 3,
                OperationState.RollbackCheckpointCreated,
                stepId: null,
                appliedStepIds: []));
    }

    [Fact]
    public void Applied_boundary_requires_its_exact_step_identifier()
    {
        var plan = PlanFixture.Create();

        Assert.Throws<ArgumentException>(() =>
            DurableOperationBoundary.Create(
                plan.PlanId,
                DurableOperationFacts.From(plan).WithBackupDigest("BACKUP-DIGEST"),
                revision: 5,
                OperationState.Applied,
                stepId: null,
                appliedStepIds: []));
    }

    [Fact]
    public void Restore_finalization_facts_cannot_appear_on_a_non_restore_transition()
    {
        var plan = PlanFixture.Create(risk: RiskLevel.High, requiresRestorePoint: true);
        var facts = DurableOperationFacts.From(plan).WithBackupDigest("BACKUP-DIGEST");
        var beginReturnedAt = new DateTimeOffset(2026, 7, 13, 0, 1, 0, TimeSpan.Zero);
        var begun = RestorePointTransitionFacts.Create(
            Guid.Parse("d572f642-1e9b-4e77-a053-1aa781e4dcb9"),
            "Winora restore point",
            PlanFixture.Fingerprint("inventory-before"),
            PlanFixture.Fingerprint("inventory-after"),
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.VerifiedNewAndOwned,
            RestorePointFinalizationMode.None,
            beginReturnedAt,
            null,
            RestorePointApiStatus.NotCalled);
        var finalized = RestorePointTransitionFacts.Create(
            begun.CorrelationId,
            begun.Description,
            begun.PreBeginInventoryFingerprint,
            begun.PostBeginInventoryFingerprint,
            begun.SequenceNumber,
            begun.BeginApiStatus,
            begun.OwnershipStatus,
            RestorePointFinalizationMode.Normal,
            begun.BeginReturnedAtUtc,
            beginReturnedAt.AddMinutes(1),
            RestorePointApiStatus.Succeeded);

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                facts,
                expectedRevision: 6,
                OperationState.Applied,
                OperationState.Verified,
                stepId: plan.Steps[0].StepId,
                beginReturnedAt.AddMinutes(2),
                finalized,
                begun,
                facts));
    }

    [Fact]
    public void Restore_required_applied_transition_cannot_drop_the_active_lifecycle()
    {
        var plan = PlanFixture.Create(risk: RiskLevel.High, requiresRestorePoint: true);
        var facts = DurableOperationFacts.From(plan).WithBackupDigest("BACKUP-DIGEST");

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                facts,
                expectedRevision: 5,
                OperationState.Applying,
                OperationState.Applied,
                stepId: plan.Steps[0].StepId,
                new DateTimeOffset(2026, 7, 13, 0, 2, 0, TimeSpan.Zero),
                previousFacts: facts));
    }

    [Theory]
    [InlineData(OperationState.Applied)]
    [InlineData(OperationState.Verified)]
    [InlineData(OperationState.ApplyStepNotApplied)]
    [InlineData(OperationState.ApplyFailedNoChanges)]
    [InlineData(OperationState.PartiallyAppliedRecoveryRequired)]
    [InlineData(OperationState.VerificationFailedRollbackOffered)]
    [InlineData(OperationState.RecoveryConflictExternalDrift)]
    public void Restore_required_mutation_boundary_cannot_drop_lifecycle_facts(OperationState state)
    {
        var plan = PlanFixture.Create(risk: RiskLevel.High, requiresRestorePoint: true);
        var facts = DurableOperationFacts.From(plan).WithBackupDigest("BACKUP-DIGEST");
        var stepId = OperationTransition.RequiresExactStep(state) ? plan.Steps[0].StepId : null;

        Assert.Throws<ArgumentException>(() =>
            DurableOperationBoundary.Create(
                plan.PlanId,
                facts,
                revision: 6,
                state,
                stepId,
                appliedStepIds: []));
    }

    [Fact]
    public void Restore_point_failed_no_changes_rejects_evidence_of_an_owned_new_sequence()
    {
        var plan = PlanFixture.Create(
            risk: RiskLevel.Informational,
            rollback: RollbackCapability.NotApplicable,
            backup: BackupRequirement.NotApplicable,
            kind: ChangePlanKind.ManualRestorePointArtifact);
        var correlation = Guid.Parse("d572f642-1e9b-4e77-a053-1aa781e4dcb9");
        var facts = DurableOperationFacts.From(plan);
        var requested = RestorePointTransitionFacts.Create(
            correlation,
            "Winora restore point",
            PlanFixture.Fingerprint("inventory-before"),
            null,
            null,
            RestorePointApiStatus.NotCalled,
            RestorePointOwnershipStatus.NotChecked,
            RestorePointFinalizationMode.None,
            null,
            null,
            RestorePointApiStatus.NotCalled);
        var contradictoryFailure = RestorePointTransitionFacts.Create(
            correlation,
            "Winora restore point",
            PlanFixture.Fingerprint("inventory-before"),
            PlanFixture.Fingerprint("inventory-after"),
            42,
            RestorePointApiStatus.KnownFailure,
            RestorePointOwnershipStatus.VerifiedNewAndOwned,
            RestorePointFinalizationMode.None,
            new DateTimeOffset(2026, 7, 13, 0, 1, 0, TimeSpan.Zero),
            null,
            RestorePointApiStatus.NotCalled);

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                facts,
                expectedRevision: 3,
                OperationState.RestorePointBeginRequested,
                OperationState.RestorePointFailedNoChanges,
                stepId: null,
                new DateTimeOffset(2026, 7, 13, 0, 1, 1, TimeSpan.Zero),
                contradictoryFailure,
                requested,
                facts));
    }

    [Fact]
    public void Reused_restore_point_cannot_be_classified_no_changes_after_cancel_finalization()
    {
        var plan = PlanFixture.Create(
            risk: RiskLevel.Informational,
            rollback: RollbackCapability.NotApplicable,
            backup: BackupRequirement.NotApplicable,
            kind: ChangePlanKind.ManualRestorePointArtifact);
        var facts = DurableOperationFacts.From(plan);
        var returnedAt = new DateTimeOffset(2026, 7, 13, 0, 1, 0, TimeSpan.Zero);
        var returned = RestorePointTransitionFacts.Create(
            Guid.Parse("d572f642-1e9b-4e77-a053-1aa781e4dcb9"),
            "Winora restore point",
            PlanFixture.Fingerprint("inventory-before"),
            null,
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.NotChecked,
            RestorePointFinalizationMode.None,
            returnedAt,
            null,
            RestorePointApiStatus.NotCalled);
        var cancelledPreexisting = RestorePointTransitionFacts.Create(
            returned.CorrelationId,
            returned.Description,
            returned.PreBeginInventoryFingerprint,
            PlanFixture.Fingerprint("inventory-after"),
            returned.SequenceNumber,
            returned.BeginApiStatus,
            RestorePointOwnershipStatus.ReusedOrPreexisting,
            RestorePointFinalizationMode.Cancelled,
            returned.BeginReturnedAtUtc,
            returnedAt.AddMinutes(1),
            RestorePointApiStatus.Succeeded);

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                facts,
                expectedRevision: 4,
                OperationState.RestorePointBeginReturnedUnverified,
                OperationState.RestorePointFailedNoChanges,
                stepId: null,
                returnedAt.AddMinutes(1),
                cancelledPreexisting,
                returned,
                facts));
    }

    [Fact]
    public void Reused_restore_point_is_failed_no_changes_only_after_normal_close()
    {
        var plan = PlanFixture.Create(
            risk: RiskLevel.Informational,
            rollback: RollbackCapability.NotApplicable,
            backup: BackupRequirement.NotApplicable,
            kind: ChangePlanKind.ManualRestorePointArtifact);
        var facts = DurableOperationFacts.From(plan);
        var returnedAt = new DateTimeOffset(2026, 7, 13, 0, 1, 0, TimeSpan.Zero);
        var returned = RestorePointTransitionFacts.Create(
            Guid.Parse("d572f642-1e9b-4e77-a053-1aa781e4dcb9"),
            "Winora restore point",
            PlanFixture.Fingerprint("inventory-before"),
            null,
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.NotChecked,
            RestorePointFinalizationMode.None,
            returnedAt,
            null,
            RestorePointApiStatus.NotCalled);
        var normallyClosedPreexisting = RestorePointTransitionFacts.Create(
            returned.CorrelationId,
            returned.Description,
            returned.PreBeginInventoryFingerprint,
            PlanFixture.Fingerprint("inventory-after"),
            returned.SequenceNumber,
            returned.BeginApiStatus,
            RestorePointOwnershipStatus.ReusedOrPreexisting,
            RestorePointFinalizationMode.Normal,
            returned.BeginReturnedAtUtc,
            returnedAt.AddMinutes(1),
            RestorePointApiStatus.Succeeded);

        var transition = OperationTransition.Create(
            plan.PlanId,
            facts,
            expectedRevision: 4,
            OperationState.RestorePointBeginReturnedUnverified,
            OperationState.RestorePointFailedNoChanges,
            stepId: null,
            returnedAt.AddMinutes(1),
            normallyClosedPreexisting,
            returned,
            facts);

        Assert.Equal(OperationState.RestorePointFailedNoChanges, transition.State);
    }

    [Fact]
    public void Rolling_back_boundary_requires_the_exact_completed_reverse_prefix()
    {
        var changePlan = PlanFixture.Create(
            steps: [PlanFixture.Step("first"), PlanFixture.Step("second"), PlanFixture.Step("third")]);
        var rollback = RollbackFixture.Create(changePlan);
        var facts = DurableOperationFacts.From(rollback)
            .WithRecoveryCheckpoint("checkpoint", "CHECKPOINT-DIGEST");

        Assert.Throws<ArgumentException>(() =>
            DurableOperationBoundary.Create(
                rollback.RollbackId,
                facts,
                revision: 8,
                OperationState.RollingBack,
                stepId: "second",
                appliedStepIds: []));
    }

    [Fact]
    public void Known_normal_finalization_failure_does_not_advertise_a_cancel_retry()
    {
        Assert.False(OperationStatePolicy.CanTransition(
            OperationState.RestorePointFinalizeFailedRecoveryRequired,
            OperationState.RestorePointCancelRequested));
    }

    [Fact]
    public void Partial_support_is_representable_and_blocks_direct_apply()
    {
        Assert.True(Enum.TryParse<SupportStatus>("Partial", out var partial));
        var plan = PlanFixture.Create(support: partial);

        Assert.False(ChangeSafetyPolicy.Evaluate(
            plan,
            CapabilityFixture.Supported(plan.SourceFingerprint)).IsAllowed);
    }

    [Fact]
    public void Plan_rejects_an_empty_identity()
    {
        Assert.Throws<ArgumentException>(() => PlanFixture.Create(planId: Guid.Empty));
    }

    [Fact]
    public void Rollback_plan_rejects_an_empty_identity()
    {
        var plan = PlanFixture.Create();

        Assert.Throws<ArgumentException>(() =>
            RollbackPlan.Create(
                Guid.Empty,
                plan,
                "BACKUP-DIGEST",
                PlanFixture.Fingerprint("aggregate-applied"),
                PlanFixture.Fingerprint("aggregate-backup")));
    }

    [Fact]
    public void Confirmation_is_bound_to_the_exact_plan_digest()
    {
        var plan = PlanFixture.Create();
        var authority = new ConfirmationAuthority();
        var confirmation = authority.Confirm(plan);

        Assert.True(authority.Authorizes(confirmation, plan));
        Assert.False(authority.Authorizes(confirmation, PlanFixture.Create(title: "Changed after confirmation")));
    }

    [Theory]
    [InlineData(typeof(ConfirmationToken))]
    [InlineData(typeof(RollbackConfirmationToken))]
    public void Confirmation_capabilities_do_not_expose_a_public_self_signing_factory(Type tokenType)
    {
        Assert.DoesNotContain(
            tokenType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
            method => method.ReturnType == tokenType);
    }

    [Fact]
    public void Rollback_digest_is_unambiguous_when_facts_contain_separator_characters()
    {
        var plan = PlanFixture.Create();
        var rollbackId = Guid.Parse("223bc02f-e3d2-4e23-8797-c4b8856fac9c");
        var first = RollbackPlan.Create(
            rollbackId,
            plan,
            "backup\u001Falgorithm",
            new StateFingerprint("applied", "value"),
            PlanFixture.Fingerprint("backup"));
        var second = RollbackPlan.Create(
            rollbackId,
            plan,
            "backup",
            new StateFingerprint("algorithm\u001Fapplied", "value"),
            PlanFixture.Fingerprint("backup"));

        Assert.NotEqual(first.Digest, second.Digest);
    }

    [Fact]
    public void Plan_snapshots_the_step_collection()
    {
        var steps = new List<ChangeStep> { PlanFixture.Step("first") };
        var plan = PlanFixture.Create(steps: steps);

        steps.Add(PlanFixture.Step("second"));

        Assert.Single(plan.Steps);
        Assert.True(plan.HasValidDigest);
    }

    [Fact]
    public void Plan_rejects_a_blank_step_identifier()
    {
        Assert.Throws<ArgumentException>(() =>
            PlanFixture.Create(steps: [PlanFixture.Step(string.Empty)]));
    }

    [Fact]
    public void Plan_rejects_duplicate_step_identifiers()
    {
        Assert.Throws<ArgumentException>(() =>
            PlanFixture.Create(steps: [PlanFixture.Step("duplicate"), PlanFixture.Step("duplicate")]));
    }

    [Theory]
    [InlineData(OperationState.Planned, OperationState.Prepared)]
    [InlineData(OperationState.Prepared, OperationState.BackupCreated)]
    [InlineData(OperationState.BackupCreated, OperationState.Applying)]
    [InlineData(OperationState.Applying, OperationState.Applied)]
    [InlineData(OperationState.Applied, OperationState.Verified)]
    [InlineData(OperationState.Verified, OperationState.Applying)]
    [InlineData(OperationState.Verified, OperationState.Completed)]
    [InlineData(OperationState.RollbackPlanned, OperationState.RollbackPrepared)]
    [InlineData(OperationState.RollbackPrepared, OperationState.RollbackCheckpointCreated)]
    [InlineData(OperationState.RollbackCheckpointCreated, OperationState.RollingBack)]
    [InlineData(OperationState.RollingBack, OperationState.RollbackApplied)]
    [InlineData(OperationState.RollbackApplied, OperationState.RollbackVerified)]
    [InlineData(OperationState.RollbackVerified, OperationState.RollingBack)]
    [InlineData(OperationState.RollbackVerified, OperationState.RolledBack)]
    public void Allowed_transition_is_accepted(OperationState from, OperationState to)
    {
        Assert.True(OperationStatePolicy.CanTransition(from, to));
    }

    [Theory]
    [InlineData(OperationState.Planned, OperationState.CanceledNoChanges)]
    [InlineData(OperationState.Prepared, OperationState.CanceledNoChanges)]
    [InlineData(OperationState.BackupCreated, OperationState.CanceledNoChanges)]
    [InlineData(OperationState.Planned, OperationState.Unsupported)]
    [InlineData(OperationState.Planned, OperationState.PlanInvalidatedNoChanges)]
    [InlineData(OperationState.Prepared, OperationState.BackupFailedNoChanges)]
    [InlineData(OperationState.Applying, OperationState.ApplyStepNotApplied)]
    [InlineData(OperationState.Applying, OperationState.RecoveryConflictExternalDrift)]
    [InlineData(OperationState.Applied, OperationState.VerificationFailedRollbackOffered)]
    [InlineData(OperationState.Verified, OperationState.PartiallyAppliedRecoveryRequired)]
    [InlineData(OperationState.RollingBack, OperationState.AlreadyRestored)]
    [InlineData(OperationState.RollingBack, OperationState.RecoveryConflictExternalDrift)]
    [InlineData(OperationState.RollbackApplied, OperationState.RollbackFailedRecoveryRequired)]
    public void Recovery_or_terminal_transition_is_accepted(OperationState from, OperationState to)
    {
        Assert.True(OperationStatePolicy.CanTransition(from, to));
    }

    [Fact]
    public void Applying_cannot_skip_to_completed()
    {
        Assert.False(OperationStatePolicy.CanTransition(OperationState.Applying, OperationState.Completed));
    }

    [Theory]
    [InlineData(OperationState.RestorePointBeginRequested, OperationState.RestorePointBeginReturnedUnverified)]
    [InlineData(OperationState.RestorePointBeginReturnedUnverified, OperationState.RestorePointBegun)]
    [InlineData(OperationState.RestorePointBegun, OperationState.RestorePointEndRequested)]
    [InlineData(OperationState.RestorePointBegun, OperationState.RestorePointCancelRequested)]
    [InlineData(OperationState.RestorePointEndRequested, OperationState.RestorePointEnded)]
    [InlineData(OperationState.RestorePointCancelRequested, OperationState.RestorePointCancelled)]
    [InlineData(OperationState.RestorePointEndRequested, OperationState.RestorePointFinalizeFailedRecoveryRequired)]
    [InlineData(OperationState.RestorePointEndRequested, OperationState.RestorePointFinalizeOutcomeUnknown)]
    [InlineData(OperationState.RestorePointFinalizeOutcomeUnknown, OperationState.RestorePointRecoveryRequired)]
    public void Restore_point_lifecycle_transition_is_accepted(OperationState from, OperationState to)
    {
        Assert.True(OperationStatePolicy.CanTransition(from, to));
    }

    [Theory]
    [InlineData(OperationState.PartiallyAppliedRecoveryRequired, OperationState.RestorePointEndRequested)]
    [InlineData(OperationState.RestorePointCancelled, OperationState.CanceledNoChanges)]
    [InlineData(OperationState.RestorePointEnded, OperationState.PartiallyAppliedRecoveryRequired)]
    public void Restore_point_partial_and_startup_recovery_transition_is_accepted(
        OperationState from,
        OperationState to)
    {
        Assert.True(OperationStatePolicy.CanTransition(from, to));
    }

    [Theory]
    [InlineData(OperationState.Applying, OperationState.Applying)]
    [InlineData(OperationState.Applied, OperationState.Completed)]
    [InlineData(OperationState.Prepared, OperationState.Applying)]
    [InlineData(OperationState.RestorePointBeginRequested, OperationState.RestorePointBegun)]
    [InlineData(OperationState.RestorePointEndRequested, OperationState.RestorePointRecoveryRequired)]
    [InlineData(OperationState.RestorePointCancelRequested, OperationState.RestorePointRecoveryRequired)]
    [InlineData(OperationState.RollingBack, OperationState.RolledBack)]
    public void Skipped_or_repeated_transition_is_rejected(OperationState from, OperationState to)
    {
        Assert.False(OperationStatePolicy.CanTransition(from, to));
    }

    [Theory]
    [InlineData(SupportStatus.Unknown, RollbackCapability.Full, BackupRequirement.Required, RiskLevel.Low, true)]
    [InlineData(SupportStatus.Unsupported, RollbackCapability.Full, BackupRequirement.Required, RiskLevel.Low, true)]
    [InlineData(SupportStatus.Guided, RollbackCapability.Full, BackupRequirement.Required, RiskLevel.Low, true)]
    [InlineData(SupportStatus.UnsupportedForSafeMutation, RollbackCapability.Full, BackupRequirement.Required, RiskLevel.Low, true)]
    [InlineData(SupportStatus.Supported, RollbackCapability.Partial, BackupRequirement.Required, RiskLevel.Low, true)]
    [InlineData(SupportStatus.Supported, RollbackCapability.NotAvailable, BackupRequirement.Required, RiskLevel.Low, true)]
    [InlineData(SupportStatus.Supported, RollbackCapability.Full, BackupRequirement.Required, RiskLevel.High, false)]
    public void Unsafe_plan_facts_block_direct_apply(
        SupportStatus support,
        RollbackCapability rollback,
        BackupRequirement backup,
        RiskLevel risk,
        bool requiresRestorePoint)
    {
        var plan = PlanFixture.Create(
            support: support,
            rollback: rollback,
            backup: backup,
            risk: risk,
            requiresRestorePoint: requiresRestorePoint);
        var capability = CapabilityFixture.Supported(plan.SourceFingerprint);

        Assert.False(ChangeSafetyPolicy.Evaluate(plan, capability).IsAllowed);
    }

    [Theory]
    [InlineData(false, true, true, true, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, true, true, false)]
    public void Missing_runtime_safety_capability_blocks_direct_apply(
        bool isWritable,
        bool hasBackup,
        bool hasVerification,
        bool hasRollback,
        bool hasConditionalMutation)
    {
        var plan = PlanFixture.Create();
        var capability = CapabilityFixture.Supported(plan.SourceFingerprint) with
        {
            IsWritable = isWritable,
            IsBackupAvailable = hasBackup,
            IsVerificationAvailable = hasVerification,
            IsRollbackAvailable = hasRollback,
            IsConditionalMutationAvailable = hasConditionalMutation,
        };

        Assert.False(ChangeSafetyPolicy.Evaluate(plan, capability).IsAllowed);
    }

    [Fact]
    public void Runtime_privilege_change_blocks_the_confirmed_plan()
    {
        var plan = PlanFixture.Create();
        var capability = CapabilityFixture.Supported(plan.SourceFingerprint) with
        {
            RequiredPrivilege = PrivilegeRequirement.Administrator,
        };

        Assert.False(ChangeSafetyPolicy.Evaluate(plan, capability).IsAllowed);
    }

    [Fact]
    public void Informational_safety_artifact_may_use_not_applicable_backup_and_rollback()
    {
        var plan = PlanFixture.Create(
            risk: RiskLevel.Informational,
            rollback: RollbackCapability.NotApplicable,
            backup: BackupRequirement.NotApplicable,
            kind: ChangePlanKind.ManualRestorePointArtifact);
        var capability = CapabilityFixture.Supported(plan.SourceFingerprint) with
        {
            IsBackupAvailable = false,
            IsRollbackAvailable = false,
        };

        Assert.True(ChangeSafetyPolicy.Evaluate(plan, capability).IsAllowed);
    }

    [Fact]
    public void Ordinary_operation_cannot_claim_the_safety_artifact_exemption()
    {
        var plan = PlanFixture.Create(
            risk: RiskLevel.Informational,
            rollback: RollbackCapability.NotApplicable,
            backup: BackupRequirement.NotApplicable);
        var capability = CapabilityFixture.Supported(plan.SourceFingerprint) with
        {
            IsBackupAvailable = false,
            IsRollbackAvailable = false,
        };

        Assert.False(ChangeSafetyPolicy.Evaluate(plan, capability).IsAllowed);
    }
}

internal static class PlanFixture
{
    internal static ChangePlan Create(
        string title = "Enable sample setting",
        IReadOnlyList<ChangeStep>? steps = null,
        RiskLevel risk = RiskLevel.Low,
        RollbackCapability rollback = RollbackCapability.Full,
        SupportStatus support = SupportStatus.Supported,
        BackupRequirement backup = BackupRequirement.Required,
        bool requiresRestorePoint = false,
        ChangePlanKind kind = ChangePlanKind.TargetMutation,
        Guid? planId = null)
    {
        return ChangePlan.Create(
            planId: planId ?? Guid.Parse("5fc03f71-e14e-4d2d-a439-d7f1ed964447"),
            operationId: "sample.operation",
            category: "Sample",
            title: title,
            summary: "Changes a sample value.",
            steps: steps ?? [Step("first")],
            risk: risk,
            privilege: PrivilegeRequirement.StandardUser,
            rollback: rollback,
            restart: RestartRequirement.None,
            support: support,
            sourceFingerprint: Fingerprint("source"),
            documentation: new Uri("https://learn.microsoft.com/windows/"),
            backup: backup,
            requiresRestorePoint: requiresRestorePoint,
            kind: kind);
    }

    internal static ChangeStep Step(string id)
    {
        return new ChangeStep(
            id,
            new OperationTarget($"target:{id}"),
            new DisplayValue("String", $"before:{id}"),
            new DisplayValue("String", $"after:{id}"),
            Fingerprint($"source:{id}"),
            Fingerprint($"result:{id}"),
            new VerificationProbe($"verify:{id}", $"Expected value for {id}"));
    }

    internal static StateFingerprint Fingerprint(string value) => new("SHA-256", value);

    internal static RestorePointTransitionFacts BegunRestoreFacts() =>
        RestorePointTransitionFacts.Create(
            Guid.Parse("d572f642-1e9b-4e77-a053-1aa781e4dcb9"),
            "Winora restore point",
            Fingerprint("inventory-before"),
            Fingerprint("inventory-after"),
            42,
            RestorePointApiStatus.Succeeded,
            RestorePointOwnershipStatus.VerifiedNewAndOwned,
            RestorePointFinalizationMode.None,
            new DateTimeOffset(2026, 7, 13, 0, 1, 0, TimeSpan.Zero),
            null,
            RestorePointApiStatus.NotCalled);
}

internal static class CapabilityFixture
{
    internal static OperationCapability Supported(StateFingerprint fingerprint)
    {
        return new OperationCapability(
            SupportStatus.Supported,
            PrivilegeRequirement.StandardUser,
            fingerprint,
            IsApiAvailable: true,
            IsWritable: true,
            IsBackupAvailable: true,
            IsVerificationAvailable: true,
            IsRollbackAvailable: true,
            IsConditionalMutationAvailable: true,
            BlockReason: null);
    }
}
