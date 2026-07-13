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
                new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Durable_transition_cannot_be_supplied_without_validation()
    {
        Assert.Empty(typeof(OperationTransition).GetConstructors());
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
                returned));
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
                restoreFacts));
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
                previous));
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
            DurableOperationFacts.From(plan),
            expectedRevision: 7,
            OperationState.RestorePointFinalizeFailedRecoveryRequired,
            OperationState.RestorePointEndRequested,
            stepId: null,
            failedAt.AddMinutes(1),
            retry,
            failed);

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
                DurableOperationFacts.From(plan),
                expectedRevision: 4,
                OperationState.RestorePointBeginReturnedUnverified,
                OperationState.RestorePointFailedNoChanges,
                stepId: null,
                returnedAt.AddSeconds(1),
                ambiguous,
                returned));
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
    public void Confirmation_is_bound_to_the_exact_plan_digest()
    {
        var plan = PlanFixture.Create();
        var confirmation = ConfirmationToken.Create(plan);

        Assert.True(confirmation.Authorizes(plan));
        Assert.False(confirmation.Authorizes(PlanFixture.Create(title: "Changed after confirmation")));
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
    [InlineData(OperationState.RestorePointFinalizeFailedRecoveryRequired, OperationState.RestorePointEndRequested)]
    [InlineData(OperationState.RestorePointFinalizeFailedRecoveryRequired, OperationState.RestorePointCancelRequested)]
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
        ChangePlanKind kind = ChangePlanKind.TargetMutation)
    {
        return ChangePlan.Create(
            planId: Guid.Parse("5fc03f71-e14e-4d2d-a439-d7f1ed964447"),
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
