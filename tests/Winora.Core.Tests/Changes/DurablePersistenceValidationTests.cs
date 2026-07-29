using Winora.Core.Changes;
using Winora.Core.Contracts;
using Xunit;

namespace Winora.Core.Tests.Changes;

public sealed class DurablePersistenceValidationTests
{
    private static readonly DateTimeOffset OccurredUtc =
        new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Durable_facts_preserve_catalog_identity_and_per_step_recovery_fingerprints()
    {
        var plan = PlanFixture.Create(
            steps: [PlanFixture.Step("first"), PlanFixture.Step("second")]);

        var facts = DurableOperationFacts.From(plan);

        Assert.Equal(plan.OperationId, facts.CatalogOperationId);
        Assert.Collection(
            facts.RecoverySteps,
            descriptor => AssertRecoveryDescriptor(descriptor, plan.Steps[0]),
            descriptor => AssertRecoveryDescriptor(descriptor, plan.Steps[1]));

        var rehydrated = DurableOperationFacts.Rehydrate(
            facts.CatalogOperationId,
            facts.PlanDigest,
            facts.SourceFingerprint,
            facts.OrderedStepIds,
            facts.RecoverySteps.Select(descriptor =>
                DurableStepRecoveryDescriptor.Rehydrate(
                    descriptor.StepId,
                    descriptor.RecoveryKey,
                    descriptor.SourceFingerprint,
                    descriptor.ResultFingerprint)).ToArray(),
            facts.Privilege,
            facts.Risk,
            facts.Rollback,
            facts.Backup,
            facts.RequiresRestorePoint,
            facts.Kind,
            facts.BackupId,
            facts.BackupDigest,
            facts.RecoveryCheckpointId,
            facts.RecoveryCheckpointDigest);

        Assert.Equal(facts.Digest, rehydrated.Digest);
        Assert.Equal(facts.CatalogOperationId, rehydrated.CatalogOperationId);
        Assert.Equal(
            facts.RecoverySteps.Select(descriptor => descriptor.Digest),
            rehydrated.RecoverySteps.Select(descriptor => descriptor.Digest));
    }

    [Fact]
    public void Rehydrated_facts_reject_recovery_steps_outside_the_persisted_order()
    {
        var plan = PlanFixture.Create(
            steps: [PlanFixture.Step("first"), PlanFixture.Step("second")]);
        var facts = DurableOperationFacts.From(plan);

        Assert.Throws<ArgumentException>(() =>
            DurableOperationFacts.Rehydrate(
                facts.CatalogOperationId,
                facts.PlanDigest,
                facts.SourceFingerprint,
                facts.OrderedStepIds,
                facts.RecoverySteps.Reverse().ToArray(),
                facts.Privilege,
                facts.Risk,
                facts.Rollback,
                facts.Backup,
                facts.RequiresRestorePoint,
                facts.Kind,
                facts.BackupId,
                facts.BackupDigest,
                facts.RecoveryCheckpointId,
                facts.RecoveryCheckpointDigest));
    }

    [Theory]
    [InlineData("../backup")]
    [InlineData("C:\\backup")]
    [InlineData("backup/child")]
    [InlineData("backup=secret")]
    public void Durable_facts_reject_non_opaque_backup_binding(string backupId)
    {
        var facts = DurableOperationFacts.From(PlanFixture.Create());

        Assert.Throws<ArgumentException>(() =>
            facts.WithBackupBinding(backupId, "BACKUP-DIGEST"));
    }

    [Theory]
    [InlineData("../target")]
    [InlineData("target/path")]
    [InlineData("target=secret")]
    public void Recovery_descriptor_rejects_path_or_value_as_recovery_key(string recoveryKey)
    {
        var step = PlanFixture.Step("first");

        Assert.Throws<ArgumentException>(() =>
            DurableStepRecoveryDescriptor.Rehydrate(
                step.StepId,
                recoveryKey,
                step.SourceFingerprint,
                step.ResultFingerprint));
    }

    [Theory]
    [InlineData("sha256", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("SHA-512", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("SHA-256", "raw-state-value")]
    [InlineData("SHA-256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("SHA-256", "C:\\Users\\name\\secret.txt")]
    public void Recovery_descriptor_rejects_noncanonical_or_raw_fingerprints(
        string algorithm,
        string value)
    {
        var valid = Fingerprint('A');

        Assert.Throws<ArgumentException>(() =>
            DurableStepRecoveryDescriptor.Rehydrate(
                "first",
                "first",
                new StateFingerprint(algorithm, value),
                valid));
    }

    [Fact]
    public void Durable_fingerprint_rejects_overlong_or_control_bearing_values()
    {
        foreach (var value in new[] { new string('A', 65), "raw\u0001state" })
        {
            Assert.Throws<ArgumentException>(() =>
                DurableFingerprintFact.Present(new StateFingerprint("SHA-256", value)));
        }
    }

    [Fact]
    public void Transition_rejects_metadata_expected_fingerprint_not_bound_to_exact_step()
    {
        var plan = PlanFixture.Create();
        var facts = DurableOperationFacts.From(plan).WithBackupBinding("backup", "BACKUP-DIGEST");
        var metadata = OperationTransitionMetadata.Create(
            DurableFingerprintFact.Present(Fingerprint('B')),
            DurableFingerprintFact.NotApplicable(),
            DurableOperationErrorCode.None);

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                facts,
                3,
                OperationState.BackupCreated,
                OperationState.Applying,
                plan.Steps[0].StepId,
                OccurredUtc,
                previousFacts: facts,
                metadata: metadata));
    }

    [Fact]
    public void Transition_accepts_a_digest_shaped_observed_third_state_when_expected_is_bound()
    {
        var plan = PlanFixture.Create();
        var facts = DurableOperationFacts.From(plan).WithBackupBinding("backup", "BACKUP-DIGEST");
        var metadata = OperationTransitionMetadata.Create(
            DurableFingerprintFact.Present(plan.Steps[0].ResultFingerprint),
            DurableFingerprintFact.Present(Fingerprint('C')),
            DurableOperationErrorCode.VerificationFailed);

        var transition = OperationTransition.Create(
            plan.PlanId,
            facts,
            4,
            OperationState.Applied,
            OperationState.VerificationFailedRollbackOffered,
            plan.Steps[0].StepId,
            OccurredUtc,
            previousFacts: facts,
            metadata: metadata);

        Assert.Equal(Fingerprint('C'), transition.Metadata.ResultFingerprint.Value);
    }

    [Fact]
    public void Transition_rejects_metadata_result_fingerprint_not_bound_to_exact_step()
    {
        var plan = PlanFixture.Create();
        var facts = DurableOperationFacts.From(plan).WithBackupBinding("backup", "BACKUP-DIGEST");
        var metadata = OperationTransitionMetadata.Create(
            DurableFingerprintFact.Present(plan.Steps[0].SourceFingerprint),
            DurableFingerprintFact.Present(Fingerprint('B')),
            DurableOperationErrorCode.None);

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                facts,
                4,
                OperationState.Applying,
                OperationState.Applied,
                plan.Steps[0].StepId,
                OccurredUtc,
                previousFacts: facts,
                metadata: metadata));
    }

    [Theory]
    [MemberData(nameof(InvalidTimestamps))]
    public void Transition_rejects_non_utc_or_default_timestamp(DateTimeOffset occurredAtUtc)
    {
        var plan = PlanFixture.Create();

        Assert.Throws<ArgumentException>(() =>
            OperationTransition.Create(
                plan.PlanId,
                DurableOperationFacts.From(plan),
                0,
                null,
                OperationState.Planned,
                null,
                occurredAtUtc));
    }

    [Theory]
    [InlineData("step\nunsafe")]
    [InlineData(" step")]
    [InlineData("step ")]
    [InlineData("C:\\Users\\name")]
    [InlineData("folder/setting")]
    [InlineData("registry:value")]
    [InlineData("token=secret")]
    [InlineData("../setting")]
    [InlineData("StepName")]
    public void Change_plan_rejects_non_opaque_step_identifier(string stepId)
    {
        Assert.Throws<ArgumentException>(() =>
            PlanFixture.Create(steps: [PlanFixture.Step(stepId)]));
    }

    [Theory]
    [InlineData("step")]
    [InlineData("step-1")]
    [InlineData("step_1")]
    [InlineData("1-step")]
    public void Change_plan_accepts_canonical_opaque_step_identifier(string stepId)
    {
        var plan = PlanFixture.Create(steps: [PlanFixture.Step(stepId)]);

        Assert.Equal(stepId, Assert.Single(plan.Steps).StepId);
    }

    [Theory]
    [InlineData("C:\\Windows\\theme")]
    [InlineData("windows/theme")]
    [InlineData("windows.theme?token=secret")]
    [InlineData("Windows.Theme")]
    [InlineData("windows..theme")]
    public void Change_plan_rejects_non_catalog_operation_identifier(string operationId)
    {
        Assert.Throws<ArgumentException>(() => PlanFixture.Create(operationId: operationId));
    }

    [Fact]
    public void Rehydrated_facts_reject_unknown_enum_value()
    {
        var plan = PlanFixture.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DurableOperationFacts.Rehydrate(
                plan.OperationId,
                plan.Digest,
                plan.SourceFingerprint,
                plan.Steps.Select(step => step.StepId).ToArray(),
                plan.Steps.Select(DurableStepRecoveryDescriptor.From).ToArray(),
                (PrivilegeRequirement)999,
                plan.Risk,
                plan.Rollback,
                plan.Backup,
                plan.RequiresRestorePoint,
                plan.Kind,
                null,
                null,
                null,
                null));
    }

    private static void AssertRecoveryDescriptor(
        DurableStepRecoveryDescriptor descriptor,
        ChangeStep step)
    {
        Assert.Equal(step.StepId, descriptor.StepId);
        Assert.Equal(step.StepId, descriptor.RecoveryKey);
        Assert.Equal(step.SourceFingerprint, descriptor.SourceFingerprint);
        Assert.Equal(step.ResultFingerprint, descriptor.ResultFingerprint);
    }

    private static StateFingerprint Fingerprint(char value) =>
        new("SHA-256", new string(value, 64));

    public static TheoryData<DateTimeOffset> InvalidTimestamps =>
        new()
        {
            default,
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.FromHours(3)),
        };
}
