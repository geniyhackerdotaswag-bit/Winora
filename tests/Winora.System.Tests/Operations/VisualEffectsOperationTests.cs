using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.System.Operations;
using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Operations;

public sealed class VisualEffectsOperationTests
{
    [Theory]
    [InlineData(VisualEffectSetting.ClientAreaAnimation, "winora.visual-effects.client-area-animation")]
    [InlineData(VisualEffectSetting.UiEffects, "winora.visual-effects.ui-effects")]
    public void Each_setting_is_its_own_operation_identity(VisualEffectSetting setting, string expected)
    {
        var operation = new VisualEffectsOperation(setting, new FakeVisualEffectsAccess());

        Assert.Equal(expected, operation.OperationId);
    }

    [Fact]
    public void The_operation_declares_a_documented_conditional_mutation_mechanism()
    {
        var operation = new VisualEffectsOperation(
            VisualEffectSetting.ClientAreaAnimation,
            new FakeVisualEffectsAccess());

        var conditional = Assert.IsAssignableFrom<IConditionalSystemMutation>(operation);
        Assert.False(string.IsNullOrWhiteSpace(conditional.ConditionalMutationMechanismId));
    }

    [Fact]
    public async Task Probe_reports_supported_for_a_readable_available_setting()
    {
        var operation = Create(out _);

        var capability = await operation.ProbeAsync(Target(operation), CancellationToken.None);

        Assert.Equal(SupportStatus.Supported, capability.Support);
        Assert.Equal(PrivilegeRequirement.StandardUser, capability.RequiredPrivilege);
        Assert.Null(capability.BlockReason);
    }

    [Fact]
    public async Task Probe_reports_an_absent_spi_action_as_unsupported()
    {
        var operation = Create(out var access);
        access.IsActionAvailable = false;

        var capability = await operation.ProbeAsync(Target(operation), CancellationToken.None);

        Assert.Equal(SupportStatus.Unsupported, capability.Support);
        Assert.False(capability.IsApiAvailable);
    }

    [Fact]
    public async Task Probe_reports_an_unreadable_setting_as_unknown()
    {
        var operation = Create(out var access);
        access.IsReadable = false;

        var capability = await operation.ProbeAsync(Target(operation), CancellationToken.None);

        Assert.Equal(SupportStatus.Unknown, capability.Support);
    }

    [Fact]
    public async Task Probe_rejects_a_target_that_belongs_to_another_operation()
    {
        var operation = Create(out _);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await operation.ProbeAsync(new OperationTarget("winora.startup.run"), CancellationToken.None));
    }

    [Fact]
    public async Task Preview_states_the_exact_current_and_proposed_values()
    {
        var operation = Create(out var access);
        access.Value = true;

        var plan = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);

        var step = Assert.Single(plan.Steps);
        Assert.Equal(operation.OperationId, step.Target.TargetId);
        Assert.Equal(VisualEffectValues.On, step.CurrentValue.Text);
        Assert.Equal(VisualEffectValues.Off, step.ProposedValue.Text);
        Assert.NotEqual(step.SourceFingerprint, step.ResultFingerprint);
        Assert.Equal(plan.SourceFingerprint, step.SourceFingerprint);
        Assert.True(plan.HasValidDigest);
    }

    [Fact]
    public async Task Preview_states_the_facts_the_confirmation_screen_must_show()
    {
        var operation = Create(out _);

        var plan = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);

        Assert.Equal(SupportStatus.Supported, plan.Support);
        Assert.Equal(PrivilegeRequirement.StandardUser, plan.Privilege);
        Assert.Equal(RollbackCapability.Full, plan.Rollback);
        Assert.Equal(RestartRequirement.None, plan.Restart);
        Assert.Equal(BackupRequirement.Required, plan.Backup);
        Assert.Equal(ChangePlanKind.TargetMutation, plan.Kind);
        Assert.False(plan.RequiresRestorePoint);
        Assert.Contains("systemparametersinfo", plan.Documentation.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_previewed_source_fingerprint_matches_the_probe()
    {
        var operation = Create(out _);

        var capability = await operation.ProbeAsync(Target(operation), CancellationToken.None);
        var plan = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);

        Assert.Equal(capability.CurrentFingerprint, plan.SourceFingerprint);
    }

    [Fact]
    public async Task Preview_does_not_touch_the_setting()
    {
        var operation = Create(out var access);

        _ = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);

        Assert.Empty(access.Writes);
        Assert.True(access.Value);
    }

    [Fact]
    public async Task Apply_writes_the_proposed_value_and_reports_the_result_fingerprint()
    {
        var operation = Create(out var access);
        var plan = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);
        var step = plan.Steps[0];

        var result = await operation.ApplyStepAsync(plan, step, CancellationToken.None);

        Assert.Equal(StepResultKind.Applied, result.Kind);
        Assert.Equal(step.ResultFingerprint, result.ObservedFingerprint);
        Assert.Equal(new[] { false }, access.Writes);
        Assert.False(access.Value);
    }

    [Fact]
    public async Task An_external_change_after_the_dry_run_is_not_overwritten()
    {
        var operation = Create(out var access);
        var plan = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);
        var step = plan.Steps[0];

        // An unrelated application turns the effect off between confirmation and apply.
        access.Value = false;

        var result = await operation.ApplyStepAsync(plan, step, CancellationToken.None);

        Assert.Equal(StepResultKind.NotApplied, result.Kind);
        Assert.Empty(access.Writes);
        Assert.False(access.Value);
    }

    [Fact]
    public async Task A_failed_documented_call_reports_that_no_change_was_made()
    {
        var operation = Create(out var access);
        var plan = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);
        access.WriteOutcome = VisualEffectWriteOutcome.NotWritten;

        var result = await operation.ApplyStepAsync(plan, plan.Steps[0], CancellationToken.None);

        Assert.Equal(StepResultKind.NotApplied, result.Kind);
        Assert.True(access.Value);
    }

    [Fact]
    public async Task An_ambiguous_documented_call_reports_an_unknown_outcome()
    {
        var operation = Create(out var access);
        var plan = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);
        access.WriteOutcome = VisualEffectWriteOutcome.OutcomeUnknown;

        var result = await operation.ApplyStepAsync(plan, plan.Steps[0], CancellationToken.None);

        Assert.Equal(StepResultKind.FailedOutcomeUnknown, result.Kind);
    }

    [Fact]
    public async Task A_readback_that_contradicts_a_successful_write_reports_an_unknown_outcome()
    {
        var operation = Create(out var access);
        var plan = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);
        access.AfterWrite = () => access.Value = true;

        var result = await operation.ApplyStepAsync(plan, plan.Steps[0], CancellationToken.None);

        Assert.Equal(StepResultKind.FailedOutcomeUnknown, result.Kind);
    }

    [Fact]
    public async Task Apply_rejects_a_step_that_belongs_to_another_target()
    {
        var operation = Create(out var access);
        var plan = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);
        var foreign = plan.Steps[0] with { Target = new OperationTarget("winora.startup.run") };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await operation.ApplyStepAsync(plan, foreign, CancellationToken.None));

        Assert.Empty(access.Writes);
    }

    [Fact]
    public async Task Verification_is_an_independent_read_of_the_applied_value()
    {
        var operation = Create(out var access);
        var plan = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);
        var step = plan.Steps[0];
        _ = await operation.ApplyStepAsync(plan, step, CancellationToken.None);

        var verification = await operation.VerifyStepAsync(plan, step, CancellationToken.None);

        Assert.True(verification.IsVerified);
        Assert.Equal(step.ResultFingerprint, verification.ObservedFingerprint);

        // Verification re-reads; it never writes again.
        Assert.Single(access.Writes);
    }

    [Fact]
    public async Task Verification_fails_when_the_value_changed_again_after_apply()
    {
        var operation = Create(out var access);
        var plan = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);
        var step = plan.Steps[0];
        _ = await operation.ApplyStepAsync(plan, step, CancellationToken.None);
        access.Value = true;

        var verification = await operation.VerifyStepAsync(plan, step, CancellationToken.None);

        Assert.False(verification.IsVerified);
        Assert.False(string.IsNullOrWhiteSpace(verification.Detail));
    }

    [Fact]
    public async Task Rollback_restores_the_exact_source_value()
    {
        var operation = Create(out var access);
        var plan = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);
        var step = plan.Steps[0];
        _ = await operation.ApplyStepAsync(plan, step, CancellationToken.None);
        var rollback = Rollback(plan, step);

        var result = await operation.RollbackStepAsync(rollback, step, CancellationToken.None);

        Assert.Equal(StepResultKind.Applied, result.Kind);
        Assert.Equal(step.SourceFingerprint, result.ObservedFingerprint);
        Assert.True(access.Value);
    }

    [Fact]
    public async Task Rollback_is_idempotent_and_writes_only_once()
    {
        var operation = Create(out var access);
        var plan = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);
        var step = plan.Steps[0];
        _ = await operation.ApplyStepAsync(plan, step, CancellationToken.None);
        var rollback = Rollback(plan, step);

        _ = await operation.RollbackStepAsync(rollback, step, CancellationToken.None);
        var second = await operation.RollbackStepAsync(rollback, step, CancellationToken.None);

        Assert.Equal(StepResultKind.AlreadyRestored, second.Kind);
        Assert.Equal(step.SourceFingerprint, second.ObservedFingerprint);
        Assert.Equal(new[] { false, true }, access.Writes);
    }

    [Fact]
    public async Task Rollback_after_no_apply_reports_already_restored_without_writing()
    {
        var operation = Create(out var access);
        var plan = await operation.PreviewAsync(Draft(operation, on: false), CancellationToken.None);
        var step = plan.Steps[0];
        var rollback = Rollback(plan, step);

        var result = await operation.RollbackStepAsync(rollback, step, CancellationToken.None);

        Assert.Equal(StepResultKind.AlreadyRestored, result.Kind);
        Assert.Empty(access.Writes);
    }

    private static VisualEffectsOperation Create(out FakeVisualEffectsAccess access)
    {
        access = new FakeVisualEffectsAccess();
        return new VisualEffectsOperation(VisualEffectSetting.ClientAreaAnimation, access);
    }

    private static OperationTarget Target(VisualEffectsOperation operation) => new(operation.OperationId);

    private static OperationDraft Draft(VisualEffectsOperation operation, bool on) => new(
        operation.OperationId,
        "winora.category.personalization",
        "Client area animation",
        "Turns the documented client-area animation preference on or off.",
        Target(operation),
        new DisplayValue(VisualEffectValues.Kind, on ? VisualEffectValues.On : VisualEffectValues.Off));

    private static RollbackPlan Rollback(ChangePlan plan, ChangeStep step) => RollbackPlan.Create(
        Guid.Parse("6f5f0b1a-1f2d-4a8e-9b3c-5d7e9f0a1b2c"),
        plan,
        "BACKUP-DIGEST",
        step.ResultFingerprint,
        step.SourceFingerprint);

    private sealed class FakeVisualEffectsAccess : IVisualEffectsAccess
    {
        public bool IsActionAvailable { get; set; } = true;

        public bool IsReadable { get; set; } = true;

        public bool Value { get; set; } = true;

        public VisualEffectWriteOutcome WriteOutcome { get; set; } = VisualEffectWriteOutcome.Written;

        public Action? AfterWrite { get; set; }

        public List<bool> Writes { get; } = [];

        public VisualEffectReading Read(VisualEffectSetting setting) =>
            new(IsActionAvailable, IsReadable, Value);

        public VisualEffectWriteOutcome Write(VisualEffectSetting setting, bool value)
        {
            Writes.Add(value);
            if (WriteOutcome != VisualEffectWriteOutcome.NotWritten)
            {
                Value = value;
            }

            AfterWrite?.Invoke();
            return WriteOutcome;
        }
    }
}
