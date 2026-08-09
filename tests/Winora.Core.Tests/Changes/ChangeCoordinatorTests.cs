using Winora.Core.Changes;
using Winora.Core.Contracts;
using Xunit;

namespace Winora.Core.Tests.Changes;

public sealed class ChangeCoordinatorTests
{
    [Fact]
    public async Task Apply_blocks_before_durable_planning_when_lease_membership_cannot_be_revalidated()
    {
        var plan = PlanFixture.Create();
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Lease.RevalidateResult = false;

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Equal(1, harness.Lease.RevalidationCount);
        Assert.Empty(harness.Journal.Transitions);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task Apply_blocks_a_misbound_mutation_lease_before_durable_planning()
    {
        var plan = PlanFixture.Create();
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Lease.OperationIdOverride = Guid.NewGuid();

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Empty(harness.Journal.Transitions);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task Successful_apply_persists_every_authorization_boundary_in_order()
    {
        var plan = PlanFixture.Create();
        var harness = CoordinatorHarness.ForApply(plan);

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Completed, result.Disposition);
        Assert.Equal(OperationState.Completed, result.DurableState);
        Assert.Equal(
            [
                OperationState.Planned,
                OperationState.Prepared,
                OperationState.BackupCreated,
                OperationState.Applying,
                OperationState.Applied,
                OperationState.Verified,
                OperationState.Completed,
            ],
            harness.Journal.Transitions.Select(transition => transition.State));
        Assert.Equal(["apply:first", "verify:first"], harness.Operation.Actions);
        Assert.Equal(ChangePlanKind.TargetMutation, harness.Journal.Transitions[0].Facts.Kind);
        Assert.Equal(
            "BACKUP-DIGEST",
            Assert.Single(harness.Journal.Transitions, transition =>
                transition.State == OperationState.BackupCreated).Facts.BackupDigest);
        var applying = Assert.Single(
            harness.Journal.Transitions,
            transition => transition.State == OperationState.Applying);
        Assert.Equal(FingerprintFactKind.Present, applying.Metadata.ExpectedFingerprint.Kind);
        Assert.Equal(plan.Steps[0].SourceFingerprint, applying.Metadata.ExpectedFingerprint.Value);
        Assert.Equal(FingerprintFactKind.NotApplicable, applying.Metadata.ResultFingerprint.Kind);
        Assert.Equal(DurableOperationErrorCode.None, applying.Metadata.ErrorCode);
        var applied = Assert.Single(
            harness.Journal.Transitions,
            transition => transition.State == OperationState.Applied);
        Assert.Equal(plan.Steps[0].SourceFingerprint, applied.Metadata.ExpectedFingerprint.Value);
        Assert.Equal(plan.Steps[0].ResultFingerprint, applied.Metadata.ResultFingerprint.Value);
        Assert.Equal(DurableOperationErrorCode.None, applied.Metadata.ErrorCode);
        var verified = Assert.Single(
            harness.Journal.Transitions,
            transition => transition.State == OperationState.Verified);
        Assert.Equal(plan.Steps[0].ResultFingerprint, verified.Metadata.ExpectedFingerprint.Value);
        Assert.Equal(plan.Steps[0].ResultFingerprint, verified.Metadata.ResultFingerprint.Value);
        Assert.False(harness.Lease.IsHeld);
    }

    [Fact]
    public async Task Foreign_durable_acknowledgement_does_not_authorize_the_system_mutation()
    {
        var plan = PlanFixture.Create();
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Journal.MisboundAcknowledgementStates.Add(OperationState.Applying);

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.DurabilityFailure, result.Disposition);
        Assert.Equal(0, harness.Operation.ApplyCount);
    }

    [Fact]
    public async Task Capability_blocking_stops_before_backup_and_mutation()
    {
        var plan = PlanFixture.Create();
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Operation.Probes.Clear();
        harness.Operation.Probes.Enqueue(
            CapabilityFixture.Supported(plan.SourceFingerprint) with
            {
                Support = SupportStatus.UnsupportedForSafeMutation,
                IsConditionalMutationAvailable = false,
                BlockReason = "No conditional Windows primitive is available.",
            });

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Equal(OperationState.Unsupported, result.DurableState);
        Assert.Equal(0, harness.Backups.ApplyBackupCount);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task Operation_without_conditional_mutation_contract_is_blocked()
    {
        var plan = PlanFixture.Create();
        var harness = CoordinatorHarness.ForApply(plan);
        var unsafeOperation = new NonConditionalOperation(harness.Operation);

        var result = await harness.Coordinator.ApplyAsync(
            unsafeOperation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Equal(OperationState.Unsupported, result.DurableState);
        Assert.Equal(0, harness.Backups.ApplyBackupCount);
    }

    [Fact]
    public async Task Restore_point_required_plan_cannot_bypass_the_restore_lifecycle()
    {
        var plan = PlanFixture.Create(risk: RiskLevel.High, requiresRestorePoint: true);
        var harness = CoordinatorHarness.ForApply(plan);

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Equal(OperationState.Unsupported, result.DurableState);
        Assert.Equal(0, harness.Backups.ApplyBackupCount);
        Assert.DoesNotContain(harness.Journal.Transitions, transition =>
            transition.State == OperationState.Applying);
    }

    [Fact]
    public async Task Manual_safety_artifact_cannot_enter_the_target_mutation_backup_pipeline()
    {
        var plan = PlanFixture.Create(
            risk: RiskLevel.Informational,
            rollback: RollbackCapability.NotApplicable,
            backup: BackupRequirement.NotApplicable,
            kind: ChangePlanKind.ManualRestorePointArtifact);
        var harness = CoordinatorHarness.ForApply(plan);

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Equal(OperationState.Unsupported, result.DurableState);
        Assert.Equal(0, harness.Backups.ApplyBackupCount);
        Assert.DoesNotContain(harness.Journal.Transitions, transition =>
            transition.State == OperationState.BackupCreated);
    }

    [Fact]
    public async Task Confirmation_for_another_digest_does_not_create_a_journal()
    {
        var plan = PlanFixture.Create();
        var harness = CoordinatorHarness.ForApply(plan);
        var otherConfirmation = harness.Confirmation.Confirm(PlanFixture.Create(title: "Other"));

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            otherConfirmation,
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Empty(harness.Journal.Transitions);
        Assert.Equal(0, harness.Lease.AcquisitionCount);
    }

    [Fact]
    public async Task Confirmation_issued_by_an_untrusted_authority_does_not_authorize_apply()
    {
        var plan = PlanFixture.Create();
        var harness = CoordinatorHarness.ForApply(plan);
        var untrustedAuthority = new ConfirmationAuthority();

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            untrustedAuthority.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Empty(harness.Journal.Transitions);
        Assert.Equal(0, harness.Lease.AcquisitionCount);
    }

    [Fact]
    public async Task Fingerprint_drift_before_backup_invalidates_without_mutation()
    {
        var plan = PlanFixture.Create();
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Operation.Probes.Clear();
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(PlanFixture.Fingerprint("drift")));

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Invalidated, result.Disposition);
        Assert.Equal(OperationState.PlanInvalidatedNoChanges, result.DurableState);
        Assert.Equal(0, harness.Backups.ApplyBackupCount);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task Backup_must_bind_verified_captured_and_live_fingerprints_to_the_plan()
    {
        var plan = PlanFixture.Create();
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Backups.ApplyReceiptFactory = current =>
            BackupReceipt.Verified(
                "backup",
                "BACKUP-DIGEST",
                current.Digest,
                PlanFixture.Fingerprint("captured-drift"),
                current.SourceFingerprint);

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.BackupFailed, result.Disposition);
        Assert.Equal(OperationState.BackupFailedNoChanges, result.DurableState);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task Cancellation_after_verified_backup_stops_before_the_first_applying_transition()
    {
        var plan = PlanFixture.Create();
        using var cancellation = new CancellationTokenSource();
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Backups.AfterApplyBackup = cancellation.Cancel;

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            cancellation.Token);

        Assert.Equal(CoordinatorDisposition.Canceled, result.Disposition);
        Assert.Equal(OperationState.CanceledNoChanges, result.DurableState);
        Assert.DoesNotContain(harness.Journal.Transitions, transition => transition.State == OperationState.Applying);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task A_non_durable_applying_transition_never_authorizes_mutation()
    {
        var plan = PlanFixture.Create();
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Journal.NonDurableStates.Add(OperationState.Applying);

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.DurabilityFailure, result.Disposition);
        Assert.Equal(OperationState.BackupCreated, result.DurableState);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task A_skipped_journal_revision_never_authorizes_mutation()
    {
        var plan = PlanFixture.Create();
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Journal.SkippedRevisionStates.Add(OperationState.Applying);

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.DurabilityFailure, result.Disposition);
        Assert.Equal(OperationState.BackupCreated, result.DurableState);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task A_second_step_waits_for_the_first_steps_durable_applied_and_verified_states()
    {
        var plan = PlanFixture.Create(steps: [PlanFixture.Step("first"), PlanFixture.Step("second")]);
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Journal.NonDurableStates.Add(OperationState.Applied);

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.DurabilityFailure, result.Disposition);
        Assert.Equal(OperationState.Applying, result.DurableState);
        Assert.Equal(["apply:first"], harness.Operation.Actions);
    }

    [Fact]
    public async Task Fingerprint_is_rechecked_before_every_step_and_late_drift_requires_partial_recovery()
    {
        var plan = PlanFixture.Create(steps: [PlanFixture.Step("first"), PlanFixture.Step("second")]);
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Operation.Probes.Clear();
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(plan.SourceFingerprint));
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(plan.Steps[0].SourceFingerprint));
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(PlanFixture.Fingerprint("external-drift")));

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Conflict, result.Disposition);
        Assert.Equal(OperationState.RecoveryConflictExternalDrift, result.DurableState);
        Assert.Equal(["apply:first", "verify:first"], harness.Operation.Actions);
        Assert.Equal(1, result.VerifiedStepCount);
    }

    [Fact]
    public async Task Step_capability_loss_is_not_misclassified_as_fingerprint_drift()
    {
        var plan = PlanFixture.Create();
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Operation.Probes.Clear();
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(plan.SourceFingerprint));
        harness.Operation.Probes.Enqueue(
            CapabilityFixture.Supported(plan.Steps[0].SourceFingerprint) with
            {
                Support = SupportStatus.UnsupportedForSafeMutation,
                IsConditionalMutationAvailable = false,
                BlockReason = "The conditional mechanism became unavailable.",
            });

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Equal(OperationState.Unsupported, result.DurableState);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task A_later_not_applied_result_records_partial_apply_and_leaves_following_steps_untouched()
    {
        var plan = PlanFixture.Create(
            steps: [PlanFixture.Step("first"), PlanFixture.Step("second"), PlanFixture.Step("third")]);
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Operation.ApplyResults.Enqueue(StepResult.Applied(plan.Steps[0].ResultFingerprint));
        harness.Operation.ApplyResults.Enqueue(StepResult.NotApplied("Conditional write declined."));

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.PartialRecoveryRequired, result.Disposition);
        Assert.Equal(OperationState.PartiallyAppliedRecoveryRequired, result.DurableState);
        Assert.Equal(["apply:first", "verify:first", "apply:second"], harness.Operation.Actions);
        Assert.Equal(1, result.VerifiedStepCount);
        var notApplied = Assert.Single(harness.Journal.Transitions, transition =>
            transition.State == OperationState.ApplyStepNotApplied);
        Assert.Equal(
            plan.Steps[1].SourceFingerprint,
            notApplied.Metadata.ExpectedFingerprint.Value);
        Assert.Equal(
            plan.Steps[1].SourceFingerprint,
            notApplied.Metadata.ResultFingerprint.Value);
    }

    [Fact]
    public async Task Verification_failure_is_durable_and_offers_but_does_not_run_rollback()
    {
        var plan = PlanFixture.Create();
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Operation.VerificationResults.Enqueue(
            VerificationResult.Failed(PlanFixture.Fingerprint("unexpected"), "Readback mismatch."));

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.VerificationFailed, result.Disposition);
        Assert.Equal(OperationState.VerificationFailedRollbackOffered, result.DurableState);
        Assert.Equal(["apply:first", "verify:first"], harness.Operation.Actions);
        Assert.Equal(0, harness.Operation.RollbackCount);
        var failed = Assert.Single(
            harness.Journal.Transitions,
            transition => transition.State == OperationState.VerificationFailedRollbackOffered);
        Assert.Equal(plan.Steps[0].ResultFingerprint, failed.Metadata.ExpectedFingerprint.Value);
        Assert.Equal(PlanFixture.Fingerprint("unexpected"), failed.Metadata.ResultFingerprint.Value);
        Assert.Equal(DurableOperationErrorCode.VerificationFailed, failed.Metadata.ErrorCode);
    }

    [Fact]
    public async Task Cancellation_after_the_boundary_is_honored_only_between_steps_as_partial_recovery()
    {
        var plan = PlanFixture.Create(steps: [PlanFixture.Step("first"), PlanFixture.Step("second")]);
        using var cancellation = new CancellationTokenSource();
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Operation.AfterApply = _ => cancellation.Cancel();

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            cancellation.Token);

        Assert.Equal(CoordinatorDisposition.PartialRecoveryRequired, result.Disposition);
        Assert.Equal(OperationState.PartiallyAppliedRecoveryRequired, result.DurableState);
        Assert.Equal(["apply:first", "verify:first"], harness.Operation.Actions);
    }

    [Fact]
    public async Task Emergency_stop_during_a_later_preflight_stops_before_that_steps_applying_transition()
    {
        var plan = PlanFixture.Create(steps: [PlanFixture.Step("first"), PlanFixture.Step("second")]);
        using var cancellation = new CancellationTokenSource();
        var harness = CoordinatorHarness.ForApply(plan);
        harness.Operation.AfterProbe = target =>
        {
            if (target == plan.Steps[1].Target)
            {
                cancellation.Cancel();
            }
        };

        var result = await harness.Coordinator.ApplyAsync(
            harness.Operation,
            plan,
            harness.Confirmation.Confirm(plan),
            cancellation.Token);

        Assert.Equal(CoordinatorDisposition.PartialRecoveryRequired, result.Disposition);
        Assert.Equal(OperationState.PartiallyAppliedRecoveryRequired, result.DurableState);
        Assert.Equal(["apply:first", "verify:first"], harness.Operation.Actions);
        Assert.DoesNotContain(harness.Journal.Transitions, transition =>
            transition.StepId == "second" && transition.State == OperationState.Applying);
    }

    [Fact]
    public async Task Uncertain_applying_matching_proposed_state_is_verified_without_replay()
    {
        var plan = PlanFixture.Create();
        var step = plan.Steps[0];
        var harness = CoordinatorHarness.ForApplyingRecovery(plan, step);
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(step.ResultFingerprint));
        harness.Operation.VerificationResults.Enqueue(VerificationResult.Passed(step.ResultFingerprint));

        var result = await harness.Coordinator.ReconcileApplyingAsync(
            harness.Operation,
            new ApplyingRecovery(plan, step),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Reconciled, result.Disposition);
        Assert.Equal(OperationState.Verified, result.DurableState);
        Assert.Equal(["verify:first"], harness.Operation.Actions);
        Assert.Equal(0, harness.Operation.ApplyCount);
    }

    [Fact]
    public async Task Recovery_blocks_when_the_lease_does_not_grant_explicit_takeover_ownership()
    {
        var plan = PlanFixture.Create();
        var step = plan.Steps[0];
        var harness = CoordinatorHarness.ForApplyingRecovery(plan, step);
        harness.Lease.GrantRecoveryOwnership = false;

        var result = await harness.Coordinator.ReconcileApplyingAsync(
            harness.Operation,
            new ApplyingRecovery(plan, step),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task Applying_recovery_requires_the_exact_bound_backup_before_probing_or_advancing()
    {
        var plan = PlanFixture.Create();
        var step = plan.Steps[0];
        var harness = CoordinatorHarness.ForApplyingRecovery(plan, step);
        harness.Backups.ApplyRecoveryReadFailure =
            new InvalidDataException("The operation backup is corrupt.");
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(step.ResultFingerprint));

        var result = await harness.Coordinator.ReconcileApplyingAsync(
            harness.Operation,
            new ApplyingRecovery(plan, step),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.PartialRecoveryRequired, result.Disposition);
        Assert.Equal(OperationState.PartiallyAppliedRecoveryRequired, result.DurableState);
        Assert.Equal(1, harness.Backups.ApplyRecoveryReadCount);
        Assert.Equal(0, harness.Operation.ProbeCount);
        Assert.Equal(0, harness.Operation.ApplyCount);
    }

    [Theory]
    [InlineData(false, OperationState.ApplyFailedNoChanges, CoordinatorDisposition.ApplyFailed)]
    [InlineData(true, OperationState.PartiallyAppliedRecoveryRequired, CoordinatorDisposition.PartialRecoveryRequired)]
    public async Task Uncertain_applying_matching_backup_is_classified_without_replay(
        bool anyPriorApplied,
        OperationState expectedState,
        CoordinatorDisposition expectedDisposition)
    {
        var plan = PlanFixture.Create(
            steps: anyPriorApplied
                ? [PlanFixture.Step("prior"), PlanFixture.Step("uncertain")]
                : [PlanFixture.Step("uncertain")]);
        var step = plan.Steps[^1];
        var harness = CoordinatorHarness.ForApplyingRecovery(plan, step, anyPriorApplied);
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(step.SourceFingerprint));

        var result = await harness.Coordinator.ReconcileApplyingAsync(
            harness.Operation,
            new ApplyingRecovery(plan, step),
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedDisposition, result.Disposition);
        Assert.Equal(expectedState, result.DurableState);
        Assert.Equal(0, harness.Operation.ApplyCount);
        var notApplied = Assert.Single(harness.Journal.Transitions, transition =>
            transition.State == OperationState.ApplyStepNotApplied);
        Assert.Equal(step.SourceFingerprint, notApplied.Metadata.ExpectedFingerprint.Value);
        Assert.Equal(step.SourceFingerprint, notApplied.Metadata.ResultFingerprint.Value);
    }

    [Fact]
    public async Task Uncertain_applying_matching_neither_known_state_becomes_external_drift()
    {
        var plan = PlanFixture.Create();
        var step = plan.Steps[0];
        var harness = CoordinatorHarness.ForApplyingRecovery(plan, step);
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(PlanFixture.Fingerprint("third-state")));

        var result = await harness.Coordinator.ReconcileApplyingAsync(
            harness.Operation,
            new ApplyingRecovery(plan, step),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Conflict, result.Disposition);
        Assert.Equal(OperationState.RecoveryConflictExternalDrift, result.DurableState);
        Assert.Equal(0, harness.Operation.ApplyCount);
    }

    [Fact]
    public async Task Applying_recovery_rejects_a_step_outside_the_canonical_plan()
    {
        var plan = PlanFixture.Create();
        var foreignStep = PlanFixture.Step("foreign");
        var harness = CoordinatorHarness.ForApplyingRecovery(plan, plan.Steps[0]);
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(foreignStep.ResultFingerprint));

        var result = await harness.Coordinator.ReconcileApplyingAsync(
            harness.Operation,
            new ApplyingRecovery(plan, foreignStep),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Equal(OperationState.Applying, result.DurableState);
        Assert.Empty(harness.Journal.Transitions);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task Applying_recovery_preserves_the_verified_restore_lifecycle()
    {
        var plan = PlanFixture.Create(risk: RiskLevel.High, requiresRestorePoint: true);
        var step = plan.Steps[0];
        var restoreFacts = PlanFixture.BegunRestoreFacts();
        var harness = CoordinatorHarness.ForApplyingRecovery(plan, step, restorePoint: restoreFacts);
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(step.ResultFingerprint));

        var result = await harness.Coordinator.ReconcileApplyingAsync(
            harness.Operation,
            new ApplyingRecovery(plan, step),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Reconciled, result.Disposition);
        Assert.All(harness.Journal.Transitions, transition =>
            Assert.Equal(restoreFacts.Digest, transition.RestorePoint?.Digest));
    }

    /// <summary>
    /// The deadlock that reached a user's machine on 2026-08-02.
    /// <para>
    /// An operation left in <see cref="OperationState.PartiallyAppliedRecoveryRequired"/> blocks the
    /// mutation lease for everything, and the one transition allowed out of that state is a
    /// rollback. But rollback asked for an ordinary lease, which is exactly what an incomplete
    /// operation refuses — so nothing could ever be applied again. Seven places in this class move
    /// an operation into that state and none moved one out.
    /// </para>
    /// <para>
    /// Rolling back the incomplete operation must therefore be allowed to take recovery ownership
    /// of it. Nothing else may: the lease still grants this only for that single operation.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rollback_of_the_incomplete_operation_takes_recovery_ownership_instead_of_deadlocking()
    {
        var changePlan = PlanFixture.Create(steps: [PlanFixture.Step("only")]);
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);

        // The very operation being rolled back is the one holding everything up.
        harness.Lease.IncompleteOperationId = changePlan.PlanId;

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.RolledBack, result.Disposition);
    }

    /// <summary>
    /// The escape hatch stays narrow: recovery ownership is for the stuck operation only, never a
    /// way for unrelated work to walk past the guard.
    /// </summary>
    [Fact]
    public async Task Rollback_of_an_unrelated_operation_is_still_refused_while_something_is_incomplete()
    {
        var changePlan = PlanFixture.Create(steps: [PlanFixture.Step("only")]);
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);

        harness.Lease.IncompleteOperationId = Guid.NewGuid();

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.OperationBusy, result.Disposition);
    }

    [Fact]
    public async Task Rollback_runs_reverse_order_and_treats_an_already_restored_step_as_success()
    {
        var changePlan = PlanFixture.Create(
            steps: [PlanFixture.Step("first"), PlanFixture.Step("second"), PlanFixture.Step("third")]);
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);
        harness.Operation.Probes.Clear();
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(rollbackPlan.AppliedFingerprint));
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(changePlan.Steps[2].ResultFingerprint));
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(changePlan.Steps[2].SourceFingerprint));
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(changePlan.Steps[1].SourceFingerprint));
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(changePlan.Steps[0].ResultFingerprint));
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(changePlan.Steps[0].SourceFingerprint));

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.RolledBack, result.Disposition);
        Assert.Equal(OperationState.RolledBack, result.DurableState);
        Assert.Equal(["rollback:third", "rollback:first"], harness.Operation.Actions);
        Assert.Contains(harness.Journal.Transitions, transition =>
            transition.State == OperationState.AlreadyRestored && transition.StepId == "second");
        var rollingBack = Assert.Single(harness.Journal.Transitions, transition =>
            transition.State == OperationState.RollingBack && transition.StepId == "third");
        Assert.Equal(
            changePlan.Steps[2].ResultFingerprint,
            rollingBack.Metadata.ExpectedFingerprint.Value);
        Assert.Equal(FingerprintFactKind.NotApplicable, rollingBack.Metadata.ResultFingerprint.Kind);
        var rollbackApplied = Assert.Single(harness.Journal.Transitions, transition =>
            transition.State == OperationState.RollbackApplied && transition.StepId == "third");
        Assert.Equal(
            changePlan.Steps[2].ResultFingerprint,
            rollbackApplied.Metadata.ExpectedFingerprint.Value);
        Assert.Equal(
            changePlan.Steps[2].SourceFingerprint,
            rollbackApplied.Metadata.ResultFingerprint.Value);
        var rollbackVerified = Assert.Single(harness.Journal.Transitions, transition =>
            transition.State == OperationState.RollbackVerified && transition.StepId == "third");
        Assert.Equal(
            changePlan.Steps[2].SourceFingerprint,
            rollbackVerified.Metadata.ExpectedFingerprint.Value);
        Assert.Equal(
            changePlan.Steps[2].SourceFingerprint,
            rollbackVerified.Metadata.ResultFingerprint.Value);
        var alreadyRestored = Assert.Single(harness.Journal.Transitions, transition =>
            transition.State == OperationState.AlreadyRestored && transition.StepId == "second");
        Assert.Equal(
            changePlan.Steps[1].SourceFingerprint,
            alreadyRestored.Metadata.ExpectedFingerprint.Value);
        Assert.Equal(
            changePlan.Steps[1].SourceFingerprint,
            alreadyRestored.Metadata.ResultFingerprint.Value);
        var checkpointTransition = Assert.Single(harness.Journal.Transitions, transition =>
            transition.State == OperationState.RollbackCheckpointCreated);
        Assert.Equal("checkpoint", checkpointTransition.Facts.RecoveryCheckpointId);
        Assert.Equal("CHECKPOINT-DIGEST", checkpointTransition.Facts.RecoveryCheckpointDigest);
    }

    [Fact]
    public async Task Rollback_capability_block_is_durable_and_stops_before_checkpoint_or_reverse_write()
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);
        harness.Operation.Probes.Clear();
        harness.Operation.Probes.Enqueue(
            CapabilityFixture.Supported(rollbackPlan.AppliedFingerprint) with
            {
                Support = SupportStatus.UnsupportedForSafeMutation,
                IsConditionalMutationAvailable = false,
                BlockReason = "No safe rollback primitive is available.",
            });

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Equal(OperationState.Unsupported, result.DurableState);
        Assert.Equal(0, harness.Backups.CheckpointCount);
        Assert.Empty(harness.Operation.Actions);
    }

    [Theory]
    [InlineData(
        RiskLevel.High,
        RollbackCapability.Full,
        BackupRequirement.Required,
        true,
        ChangePlanKind.TargetMutation)]
    [InlineData(
        RiskLevel.Informational,
        RollbackCapability.NotApplicable,
        BackupRequirement.NotApplicable,
        false,
        ChangePlanKind.ManualRestorePointArtifact)]
    public async Task Generic_rollback_cannot_execute_a_restore_lifecycle_plan(
        RiskLevel risk,
        RollbackCapability rollback,
        BackupRequirement backup,
        bool requiresRestorePoint,
        ChangePlanKind kind)
    {
        var changePlan = PlanFixture.Create(
            risk: risk,
            rollback: rollback,
            backup: backup,
            requiresRestorePoint: requiresRestorePoint,
            kind: kind);
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Equal(OperationState.Unsupported, result.DurableState);
        Assert.Equal(0, harness.Backups.ExistingReadCount);
        Assert.Equal(0, harness.Backups.CheckpointCount);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task Confirmation_issued_by_an_untrusted_authority_does_not_authorize_rollback()
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);
        var untrustedAuthority = new ConfirmationAuthority();

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            untrustedAuthority.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Empty(harness.Journal.Transitions);
        Assert.Equal(0, harness.Lease.AcquisitionCount);
    }

    [Fact]
    public async Task Rollback_operation_must_match_the_confirmed_change_plan()
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);
        harness.Operation.OperationIdValue = "different.operation";

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Equal(OperationState.Unsupported, result.DurableState);
        Assert.Equal(0, harness.Backups.CheckpointCount);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task Rollback_rechecks_each_reverse_step_and_stops_on_external_drift()
    {
        var changePlan = PlanFixture.Create(
            steps: [PlanFixture.Step("first"), PlanFixture.Step("second"), PlanFixture.Step("third")]);
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);
        harness.Operation.Probes.Clear();
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(rollbackPlan.AppliedFingerprint));
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(changePlan.Steps[2].ResultFingerprint));
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(changePlan.Steps[2].SourceFingerprint));
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(PlanFixture.Fingerprint("third-party")));

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Conflict, result.Disposition);
        Assert.Equal(OperationState.RecoveryConflictExternalDrift, result.DurableState);
        Assert.Equal(["rollback:third"], harness.Operation.Actions);
        var conflict = Assert.Single(
            harness.Journal.Transitions,
            transition => transition.State == OperationState.RecoveryConflictExternalDrift);
        Assert.Equal("second", conflict.StepId);
        Assert.Equal(
            changePlan.Steps[1].ResultFingerprint,
            conflict.Metadata.ExpectedFingerprint.Value);
        Assert.Equal(
            PlanFixture.Fingerprint("third-party"),
            conflict.Metadata.ResultFingerprint.Value);
    }

    [Fact]
    public async Task Rollback_rechecks_safety_capability_before_every_reverse_step()
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);
        harness.Operation.Probes.Clear();
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(rollbackPlan.AppliedFingerprint));
        harness.Operation.Probes.Enqueue(
            CapabilityFixture.Supported(changePlan.Steps[0].ResultFingerprint) with
            {
                Support = SupportStatus.UnsupportedForSafeMutation,
                IsConditionalMutationAvailable = false,
                BlockReason = "The conditional rollback mechanism became unavailable.",
            });

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Equal(OperationState.Unsupported, result.DurableState);
        Assert.Equal(0, harness.Operation.RollbackCount);
    }

    [Fact]
    public async Task Rollback_failure_metadata_retains_the_rolling_back_applied_precondition()
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);
        harness.Operation.RollbackResults.Enqueue(
            StepResult.NotApplied("The conditional reverse write was rejected."));

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.RollbackFailed, result.Disposition);
        var failure = Assert.Single(harness.Journal.Transitions, transition =>
            transition.State == OperationState.RollbackFailedRecoveryRequired &&
            transition.StepId == changePlan.Steps[0].StepId);
        Assert.Equal(
            changePlan.Steps[0].ResultFingerprint,
            failure.Metadata.ExpectedFingerprint.Value);
    }

    [Fact]
    public async Task Rollback_verification_conflict_metadata_expects_the_backup_postcondition()
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var step = changePlan.Steps[0];
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);
        harness.Operation.Probes.Clear();
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(rollbackPlan.AppliedFingerprint));
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(step.ResultFingerprint));
        harness.Operation.Probes.Enqueue(
            CapabilityFixture.Supported(PlanFixture.Fingerprint("post-rollback-drift")));

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Conflict, result.Disposition);
        var conflict = Assert.Single(harness.Journal.Transitions, transition =>
            transition.State == OperationState.RecoveryConflictExternalDrift &&
            transition.StepId == step.StepId);
        Assert.Equal(
            step.SourceFingerprint,
            conflict.Metadata.ExpectedFingerprint.Value);
        Assert.Equal(
            PlanFixture.Fingerprint("post-rollback-drift"),
            conflict.Metadata.ResultFingerprint.Value);
    }

    [Fact]
    public async Task Rollback_checkpoint_must_bind_captured_and_live_fingerprints_before_reverse_mutation()
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);
        harness.Backups.CheckpointReceiptFactory = current =>
            BackupReceipt.Verified(
                "checkpoint",
                "CHECKPOINT-DIGEST",
                current.Digest,
                PlanFixture.Fingerprint("captured-drift"),
                current.AppliedFingerprint);

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.RollbackFailed, result.Disposition);
        Assert.Equal(OperationState.RollbackFailedRecoveryRequired, result.DurableState);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task Rollback_requires_the_linked_backup_to_be_read_and_verified()
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);
        harness.Backups.ExistingReceiptFactory = current =>
            BackupReceipt.Verified(
                "backup",
                "DIFFERENT-BACKUP-DIGEST",
                current.ChangePlan.Digest,
                current.ChangePlan.SourceFingerprint,
                current.ChangePlan.SourceFingerprint);

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.RollbackFailed, result.Disposition);
        Assert.Equal(OperationState.RollbackFailedRecoveryRequired, result.DurableState);
        Assert.Equal(1, harness.Backups.ExistingReadCount);
        Assert.Equal(0, harness.Backups.CheckpointCount);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task Rollback_requires_the_exact_linked_backup_identifier()
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);
        harness.Backups.ExistingReceiptFactory = current =>
            BackupReceipt.Verified(
                "different-backup",
                current.BackupDigest,
                current.ChangePlan.Digest,
                current.ChangePlan.SourceFingerprint,
                current.ChangePlan.SourceFingerprint);

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.RollbackFailed, result.Disposition);
        Assert.Equal(OperationState.RollbackFailedRecoveryRequired, result.DurableState);
        Assert.Equal(0, harness.Backups.CheckpointCount);
        Assert.Empty(harness.Operation.Actions);
    }

    [Fact]
    public async Task Conditional_rollback_returning_already_restored_is_a_successful_idempotent_step()
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);
        harness.Operation.RollbackResults.Enqueue(
            StepResult.AlreadyRestored(changePlan.Steps[0].SourceFingerprint));

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.RolledBack, result.Disposition);
        Assert.Equal(OperationState.RolledBack, result.DurableState);
        Assert.Equal(["rollback:first"], harness.Operation.Actions);
        Assert.Contains(harness.Journal.Transitions, transition =>
            transition.State == OperationState.AlreadyRestored && transition.StepId == "first");
    }

    [Fact]
    public async Task Already_restored_result_must_report_the_backup_fingerprint()
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);
        harness.Operation.RollbackResults.Enqueue(
            StepResult.AlreadyRestored(PlanFixture.Fingerprint("external-drift")));

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Conflict, result.Disposition);
        Assert.Equal(OperationState.RecoveryConflictExternalDrift, result.DurableState);
        Assert.Equal(0, result.VerifiedStepCount);
    }

    [Fact]
    public async Task Repeating_rollback_when_aggregate_state_is_already_restored_is_idempotent()
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var harness = CoordinatorHarness.ForRollback(rollbackPlan);
        harness.Operation.Probes.Clear();
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(rollbackPlan.BackupFingerprint));

        var result = await harness.Coordinator.RollbackAsync(
            harness.Operation,
            rollbackPlan,
            harness.Confirmation.Confirm(rollbackPlan),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.AlreadyRestored, result.Disposition);
        Assert.Equal(OperationState.RolledBack, result.DurableState);
        Assert.Equal(0, harness.Backups.CheckpointCount);
        Assert.Equal(0, harness.Operation.RollbackCount);
        Assert.Collection(
            harness.Journal.Transitions,
            transition => Assert.Equal(OperationState.RollbackPlanned, transition.State),
            transition => Assert.Equal(OperationState.AlreadyRestored, transition.State),
            transition => Assert.Equal(OperationState.RolledBack, transition.State));
        Assert.True(OperationStatePolicy.IsTerminal(result.DurableState!.Value));
    }

    [Theory]
    [InlineData("backup", OperationState.RollbackVerified, CoordinatorDisposition.Reconciled)]
    [InlineData("applied", OperationState.RollbackFailedRecoveryRequired, CoordinatorDisposition.RollbackFailed)]
    [InlineData("other", OperationState.RecoveryConflictExternalDrift, CoordinatorDisposition.Conflict)]
    public async Task Uncertain_rolling_back_is_classified_without_replay(
        string observed,
        OperationState expectedState,
        CoordinatorDisposition expectedDisposition)
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var step = rollbackPlan.Steps[0];
        var fingerprint = observed switch
        {
            "backup" => step.SourceFingerprint,
            "applied" => step.ResultFingerprint,
            _ => PlanFixture.Fingerprint("external"),
        };
        var harness = CoordinatorHarness.ForRollingBackRecovery(rollbackPlan, step);
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(fingerprint));

        var result = await harness.Coordinator.ReconcileRollingBackAsync(
            harness.Operation,
            new RollingBackRecovery(rollbackPlan, step),
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedDisposition, result.Disposition);
        Assert.Equal(expectedState, result.DurableState);
        Assert.Equal(0, harness.Operation.RollbackCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rolling_back_recovery_requires_both_bound_artifacts_before_probing_or_advancing(
        bool failOperationBackup)
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var step = rollbackPlan.Steps[0];
        var harness = CoordinatorHarness.ForRollingBackRecovery(rollbackPlan, step);
        if (failOperationBackup)
        {
            harness.Backups.ExistingReadFailure =
                new InvalidDataException("The operation backup is missing.");
        }
        else
        {
            harness.Backups.RecoveryCheckpointReadFailure =
                new InvalidDataException("The recovery checkpoint is corrupt.");
        }

        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(step.SourceFingerprint));

        var result = await harness.Coordinator.ReconcileRollingBackAsync(
            harness.Operation,
            new RollingBackRecovery(rollbackPlan, step),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.RollbackFailed, result.Disposition);
        Assert.Equal(OperationState.RollbackFailedRecoveryRequired, result.DurableState);
        Assert.Equal(1, harness.Backups.ExistingReadCount);
        Assert.Equal(failOperationBackup ? 0 : 1, harness.Backups.RecoveryCheckpointReadCount);
        Assert.Equal(0, harness.Operation.ProbeCount);
        Assert.Equal(0, harness.Operation.RollbackCount);
    }

    [Fact]
    public async Task Rolling_back_recovery_rejects_a_step_outside_the_canonical_plan()
    {
        var changePlan = PlanFixture.Create();
        var rollbackPlan = RollbackFixture.Create(changePlan);
        var foreignStep = PlanFixture.Step("foreign");
        var harness = CoordinatorHarness.ForRollingBackRecovery(rollbackPlan, rollbackPlan.Steps[0]);
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(foreignStep.SourceFingerprint));

        var result = await harness.Coordinator.ReconcileRollingBackAsync(
            harness.Operation,
            new RollingBackRecovery(rollbackPlan, foreignStep),
            TestContext.Current.CancellationToken);

        Assert.Equal(CoordinatorDisposition.Blocked, result.Disposition);
        Assert.Equal(OperationState.RollingBack, result.DurableState);
        Assert.Empty(harness.Journal.Transitions);
        Assert.Equal(0, harness.Operation.RollbackCount);
    }
}

internal static class RollbackFixture
{
    internal static RollbackPlan Create(ChangePlan plan)
    {
        return RollbackPlan.Create(
            rollbackId: Guid.Parse("223bc02f-e3d2-4e23-8797-c4b8856fac9c"),
            changePlan: plan,
            linkedBackup: BackupReceipt.Verified(
                "backup",
                "BACKUP-DIGEST",
                plan.Digest,
                plan.SourceFingerprint,
                plan.SourceFingerprint),
            appliedFingerprint: PlanFixture.Fingerprint("aggregate-applied"));
    }
}

internal sealed class CoordinatorHarness
{
    private CoordinatorHarness(InMemoryJournal journal)
    {
        Journal = journal;
        Backups = new InMemoryBackupRepository();
        Lease = new InMemoryMutationLease();
        Clock = new FixedClock();
        Operation = new InMemoryOperation();
        Confirmation = new ConfirmationAuthority();
        Coordinator = new ChangeCoordinator(Journal, Backups, Lease, Clock, Confirmation);
    }

    internal ChangeCoordinator Coordinator { get; }

    internal InMemoryJournal Journal { get; }

    internal InMemoryBackupRepository Backups { get; }

    internal InMemoryMutationLease Lease { get; }

    internal FixedClock Clock { get; }

    internal InMemoryOperation Operation { get; }

    internal ConfirmationAuthority Confirmation { get; }

    internal static CoordinatorHarness ForApply(ChangePlan plan)
    {
        var harness = new CoordinatorHarness(new InMemoryJournal());
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(plan.SourceFingerprint));
        foreach (var step in plan.Steps)
        {
            harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(step.SourceFingerprint));
        }

        harness.Backups.ApplyReceiptFactory = current =>
            BackupReceipt.Verified(
                "backup",
                "BACKUP-DIGEST",
                current.Digest,
                current.SourceFingerprint,
                current.SourceFingerprint);
        return harness;
    }

    internal static CoordinatorHarness ForRollback(RollbackPlan plan)
    {
        var harness = new CoordinatorHarness(new InMemoryJournal());
        harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(plan.AppliedFingerprint));
        foreach (var step in plan.Steps)
        {
            harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(step.ResultFingerprint));
            harness.Operation.Probes.Enqueue(CapabilityFixture.Supported(step.SourceFingerprint));
        }

        harness.Backups.CheckpointReceiptFactory = current =>
            BackupReceipt.Verified(
                "checkpoint",
                "CHECKPOINT-DIGEST",
                current.Digest,
                current.AppliedFingerprint,
                current.AppliedFingerprint);
        harness.Backups.ExistingReceiptFactory = current =>
            BackupReceipt.Verified(
                "backup",
                current.BackupDigest,
                current.ChangePlan.Digest,
                current.ChangePlan.SourceFingerprint,
                current.ChangePlan.SourceFingerprint);
        return harness;
    }

    internal static CoordinatorHarness ForApplyingRecovery(
        ChangePlan plan,
        ChangeStep uncertainStep,
        bool anyPriorApplied = false,
        RestorePointTransitionFacts? restorePoint = null)
    {
        var appliedStepIds = anyPriorApplied
            ? plan.Steps.TakeWhile(step => step != uncertainStep).Select(step => step.StepId).ToArray()
            : [];
        var facts = DurableOperationFacts.From(plan).WithBackupBinding("backup", "BACKUP-DIGEST");
        var boundary = DurableOperationBoundary.Create(
            plan.PlanId,
            facts,
            revision: 4,
            OperationState.Applying,
            uncertainStep.StepId,
            appliedStepIds,
            restorePoint);
        return new CoordinatorHarness(new InMemoryJournal(boundary));
    }

    internal static CoordinatorHarness ForRollingBackRecovery(RollbackPlan plan, ChangeStep uncertainStep)
    {
        var facts = DurableOperationFacts.From(plan)
            .WithRecoveryCheckpoint("checkpoint", "CHECKPOINT-DIGEST");
        var boundary = DurableOperationBoundary.Create(
            plan.RollbackId,
            facts,
            revision: 8,
            OperationState.RollingBack,
            uncertainStep.StepId,
            []);
        var harness = new CoordinatorHarness(new InMemoryJournal(boundary));
        harness.Backups.ExistingReceiptFactory = current =>
            BackupReceipt.Verified(
                current.BackupId,
                current.BackupDigest,
                current.ChangePlan.Digest,
                current.ChangePlan.SourceFingerprint,
                current.ChangePlan.SourceFingerprint);
        return harness;
    }
}

internal sealed class InMemoryOperation : IOperation, IConditionalSystemMutation
{
    internal Queue<OperationCapability> Probes { get; } = new();

    internal Queue<StepResult> ApplyResults { get; } = new();

    internal Queue<VerificationResult> VerificationResults { get; } = new();

    internal Queue<StepResult> RollbackResults { get; } = new();

    internal List<string> Actions { get; } = [];

    internal Action<ChangeStep>? AfterApply { get; set; }

    internal Action<OperationTarget>? AfterProbe { get; set; }

    internal string OperationIdValue { get; set; } = "sample.operation";

    public string OperationId => OperationIdValue;

    public string ConditionalMutationMechanismId => "in-memory-compare-and-set";

    internal int ApplyCount => Actions.Count(action => action.StartsWith("apply:", StringComparison.Ordinal));

    internal int RollbackCount => Actions.Count(action => action.StartsWith("rollback:", StringComparison.Ordinal));

    internal int ProbeCount { get; private set; }

    public ValueTask<OperationCapability> ProbeAsync(
        OperationTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProbeCount++;
        var result = Probes.Dequeue();
        AfterProbe?.Invoke(target);
        return ValueTask.FromResult(result);
    }

    public ValueTask<ChangePlan> PreviewAsync(OperationDraft draft, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The coordinator tests start from an already confirmed plan.");

    public ValueTask<StepResult> ApplyStepAsync(
        ChangePlan plan,
        ChangeStep step,
        CancellationToken cancellationToken)
    {
        Assert.False(cancellationToken.CanBeCanceled);
        Actions.Add($"apply:{step.StepId}");
        var result = ApplyResults.Count == 0
            ? StepResult.Applied(step.ResultFingerprint)
            : ApplyResults.Dequeue();
        AfterApply?.Invoke(step);
        return ValueTask.FromResult(result);
    }

    public ValueTask<VerificationResult> VerifyStepAsync(
        ChangePlan plan,
        ChangeStep step,
        CancellationToken cancellationToken)
    {
        Assert.False(cancellationToken.CanBeCanceled);
        Actions.Add($"verify:{step.StepId}");
        return ValueTask.FromResult(
            VerificationResults.Count == 0
                ? VerificationResult.Passed(step.ResultFingerprint)
                : VerificationResults.Dequeue());
    }

    public ValueTask<StepResult> RollbackStepAsync(
        RollbackPlan plan,
        ChangeStep step,
        CancellationToken cancellationToken)
    {
        Assert.False(cancellationToken.CanBeCanceled);
        Actions.Add($"rollback:{step.StepId}");
        return ValueTask.FromResult(
            RollbackResults.Count == 0
                ? StepResult.Applied(step.SourceFingerprint)
                : RollbackResults.Dequeue());
    }
}

internal sealed class NonConditionalOperation(IOperation inner) : IOperation
{
    public string OperationId => inner.OperationId;

    public ValueTask<OperationCapability> ProbeAsync(OperationTarget target, CancellationToken cancellationToken) =>
        inner.ProbeAsync(target, cancellationToken);

    public ValueTask<ChangePlan> PreviewAsync(OperationDraft draft, CancellationToken cancellationToken) =>
        inner.PreviewAsync(draft, cancellationToken);

    public ValueTask<StepResult> ApplyStepAsync(ChangePlan plan, ChangeStep step, CancellationToken cancellationToken) =>
        inner.ApplyStepAsync(plan, step, cancellationToken);

    public ValueTask<VerificationResult> VerifyStepAsync(
        ChangePlan plan,
        ChangeStep step,
        CancellationToken cancellationToken) =>
        inner.VerifyStepAsync(plan, step, cancellationToken);

    public ValueTask<StepResult> RollbackStepAsync(
        RollbackPlan plan,
        ChangeStep step,
        CancellationToken cancellationToken) =>
        inner.RollbackStepAsync(plan, step, cancellationToken);
}

internal sealed class InMemoryBackupRepository : IBackupRepository
{
    internal Func<ChangePlan, BackupReceipt>? ApplyReceiptFactory { get; set; }

    internal Func<RollbackPlan, BackupReceipt>? CheckpointReceiptFactory { get; set; }

    internal Func<RollbackPlan, BackupReceipt>? ExistingReceiptFactory { get; set; }

    internal Action? AfterApplyBackup { get; set; }

    internal Exception? ApplyRecoveryReadFailure { get; set; }

    internal Exception? ExistingReadFailure { get; set; }

    internal Exception? RecoveryCheckpointReadFailure { get; set; }

    internal int ApplyBackupCount { get; private set; }

    internal int CheckpointCount { get; private set; }

    internal int ExistingReadCount { get; private set; }

    internal int ApplyRecoveryReadCount { get; private set; }

    internal int RecoveryCheckpointReadCount { get; private set; }

    public ValueTask<BackupReceipt> ReadAndVerifyOperationBackupAsync(
        ChangePlan plan,
        string backupId,
        string backupDigest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyRecoveryReadCount++;
        if (ApplyRecoveryReadFailure is not null)
        {
            throw ApplyRecoveryReadFailure;
        }

        return ValueTask.FromResult(BackupReceipt.Verified(
            backupId,
            backupDigest,
            plan.Digest,
            plan.SourceFingerprint,
            plan.SourceFingerprint));
    }

    public ValueTask<BackupReceipt> ReadAndVerifyRecoveryCheckpointAsync(
        RollbackPlan plan,
        string checkpointId,
        string checkpointDigest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecoveryCheckpointReadCount++;
        if (RecoveryCheckpointReadFailure is not null)
        {
            throw RecoveryCheckpointReadFailure;
        }

        return ValueTask.FromResult(BackupReceipt.Verified(
            checkpointId,
            checkpointDigest,
            plan.Digest,
            plan.AppliedFingerprint,
            plan.AppliedFingerprint));
    }

    public ValueTask<BackupReceipt> CreateAndVerifyAsync(ChangePlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyBackupCount++;
        var receipt = ApplyReceiptFactory!(plan);
        AfterApplyBackup?.Invoke();
        return ValueTask.FromResult(receipt);
    }

    public ValueTask<BackupReceipt> CreateRecoveryCheckpointAsync(
        RollbackPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckpointCount++;
        return ValueTask.FromResult(CheckpointReceiptFactory!(plan));
    }

    public ValueTask<BackupReceipt> ReadAndVerifyAsync(
        RollbackPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExistingReadCount++;
        if (ExistingReadFailure is not null)
        {
            throw ExistingReadFailure;
        }

        return ValueTask.FromResult(ExistingReceiptFactory!(plan));
    }
}

internal sealed class InMemoryJournal : IDurableOperationJournal
{
    private OperationState? _state;
    private long _revision;
    private readonly DurableOperationBoundary? _boundary;
    private string? _factsDigest;
    private string? _restorePointFactsDigest;
    private DurableOperationFacts? _facts;

    internal InMemoryJournal(OperationState? state = null, long revision = 0)
    {
        _state = state;
        _revision = revision;
    }

    internal InMemoryJournal(DurableOperationBoundary boundary)
    {
        _boundary = boundary;
        _state = boundary.State;
        _revision = boundary.Revision;
        _factsDigest = boundary.Facts.Digest;
        _restorePointFactsDigest = boundary.RestorePoint?.Digest;
        _facts = boundary.Facts;
    }

    internal List<OperationTransition> Transitions { get; } = [];

    internal HashSet<OperationState> NonDurableStates { get; } = [];

    internal HashSet<OperationState> SkippedRevisionStates { get; } = [];

    internal HashSet<OperationState> MisboundAcknowledgementStates { get; } = [];

    public ValueTask<IReadOnlyList<DurableOperationBoundary>> ScanIncompleteAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DurableOperationBoundary> result =
            _boundary is not null && !OperationStatePolicy.IsTerminal(_boundary.State)
                ? [_boundary]
                : [];
        return ValueTask.FromResult(result);
    }

    public ValueTask<DurableOperationBoundary?> ReadVerifiedBoundaryAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _boundary is not null && _boundary.OperationId == operationId
                ? _boundary
                : null);
    }

    public ValueTask<DurableTransitionResult> CompareAndAppendAsync(
        OperationTransition transition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (transition.ExpectedRevision != _revision ||
            transition.ExpectedState != _state ||
            !StringComparer.Ordinal.Equals(transition.ExpectedFactsDigest, _factsDigest) ||
            !StringComparer.Ordinal.Equals(
                transition.ExpectedRestorePointFactsDigest,
                _restorePointFactsDigest))
        {
            return ValueTask.FromResult(DurableTransitionResult.Rejected(_revision, _state));
        }

        if (NonDurableStates.Contains(transition.State))
        {
            return ValueTask.FromResult(DurableTransitionResult.Rejected(_revision, _state));
        }

        if (SkippedRevisionStates.Contains(transition.State))
        {
            return ValueTask.FromResult(
                DurableTransitionResult.Acknowledged(transition, _revision + 2));
        }

        if (MisboundAcknowledgementStates.Contains(transition.State))
        {
            var foreignTransition = OperationTransition.Create(
                Guid.NewGuid(),
                transition.Facts,
                transition.ExpectedRevision,
                transition.ExpectedState,
                transition.State,
                transition.StepId,
                transition.OccurredAtUtc.AddTicks(1),
                transition.RestorePoint,
                previousFacts: _facts);
            return ValueTask.FromResult(
                DurableTransitionResult.Acknowledged(foreignTransition));
        }

        _revision++;
        _state = transition.State;
        _factsDigest = transition.Facts.Digest;
        _restorePointFactsDigest = transition.RestorePoint?.Digest;
        _facts = transition.Facts;
        Transitions.Add(transition);
        return ValueTask.FromResult(DurableTransitionResult.Acknowledged(transition));
    }
}

internal sealed class InMemoryMutationLease : IMutationLease
{
    /// <summary>
    /// Models the real lease's hardest rule: while any operation is still incomplete, an ordinary
    /// acquisition is refused outright, and only a recovery takeover for that exact operation is
    /// granted. Without this the fake was more permissive than the thing it stands for, which is
    /// how a deadlock reached a user's machine unnoticed.
    /// </summary>
    internal Guid? IncompleteOperationId { get; set; }

    internal bool IsAvailable { get; set; } = true;

    internal bool IsHeld { get; private set; }

    internal int AcquisitionCount { get; private set; }

    internal bool GrantRecoveryOwnership { get; set; } = true;

    internal Guid? OperationIdOverride { get; set; }

    internal bool RevalidateResult { get; set; } = true;

    internal int RevalidationCount { get; private set; }

    public ValueTask<IMutationLeaseHandle?> TryAcquireAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (IncompleteOperationId is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquisitionCount++;
            return ValueTask.FromResult<IMutationLeaseHandle?>(null);
        }

        return TryAcquireCore(operationId, isRecoveryTakeover: false, cancellationToken);
    }

    public ValueTask<IMutationLeaseHandle?> TryAcquireRecoveryAsync(
        Guid incompleteOperationId,
        CancellationToken cancellationToken)
    {
        if (IncompleteOperationId is { } pending && pending != incompleteOperationId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquisitionCount++;
            return ValueTask.FromResult<IMutationLeaseHandle?>(null);
        }

        return TryAcquireCore(incompleteOperationId, GrantRecoveryOwnership, cancellationToken);
    }

    private ValueTask<IMutationLeaseHandle?> TryAcquireCore(
        Guid operationId,
        bool isRecoveryTakeover,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AcquisitionCount++;
        if (!IsAvailable || IsHeld)
        {
            return ValueTask.FromResult<IMutationLeaseHandle?>(null);
        }

        IsHeld = true;
        return ValueTask.FromResult<IMutationLeaseHandle?>(
            new Handle(
                this,
                Guid.NewGuid(),
                OperationIdOverride ?? operationId,
                isRecoveryTakeover));
    }

    private sealed class Handle(
        InMemoryMutationLease owner,
        Guid leaseId,
        Guid operationId,
        bool isRecoveryTakeover) : IMutationLeaseHandle
    {
        public Guid LeaseId => leaseId;

        public Guid OperationId => operationId;

        public long Epoch => 1;

        public bool IsRecoveryTakeover => isRecoveryTakeover;

        public ValueTask<bool> HeartbeatAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(owner.IsHeld);
        }

        public ValueTask<bool> RevalidateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.RevalidationCount++;
            return ValueTask.FromResult(owner.IsHeld && owner.RevalidateResult);
        }

        public ValueTask DisposeAsync()
        {
            owner.IsHeld = false;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow => new(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
}

internal static class TestContext
{
    internal static TestExecutionContext Current { get; } = new();
}

internal sealed class TestExecutionContext
{
    internal CancellationToken CancellationToken => System.Threading.CancellationToken.None;
}
