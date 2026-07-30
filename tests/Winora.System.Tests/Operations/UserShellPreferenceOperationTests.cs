using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.System.Operations;
using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Operations;

public sealed class UserShellPreferenceOperationTests
{
    private static readonly DocumentedShellValue Alignment = DocumentedShellValues.Find("TaskbarAl");

    [Fact]
    public void The_operation_identifies_its_documented_target()
    {
        var operation = new UserShellPreferenceOperation(Alignment, new StubAccess(present: false, value: null));

        Assert.Equal("winora.shell.taskbar-alignment", operation.OperationId);
        Assert.Equal("windows.registry.single-value-verified-write", operation.ConditionalMutationMechanismId);
    }

    [Fact]
    public async Task An_absent_value_probes_as_supported_and_reports_unset()
    {
        var operation = new UserShellPreferenceOperation(Alignment, new StubAccess(present: false, value: null));

        var capability = await operation.ProbeAsync(new OperationTarget(operation.OperationId), default);

        Assert.Equal(SupportStatus.Supported, capability.Support);
        Assert.Equal("unset", capability.CurrentValue?.Text);
    }

    /// <summary>
    /// The reference page lists these as REG_SZ while the live registry uses DWORDs. A probe that
    /// coerced whatever it found would write back the wrong shape, so a mismatch must block.
    /// </summary>
    [Fact]
    public async Task A_value_of_an_undocumented_kind_blocks_direct_mutation()
    {
        var access = new StubAccess(present: true, value: null) { KindMatchesDocumentation = false };
        var operation = new UserShellPreferenceOperation(Alignment, access);

        var capability = await operation.ProbeAsync(new OperationTarget(operation.OperationId), default);

        Assert.NotEqual(SupportStatus.Supported, capability.Support);
        Assert.Null(capability.CurrentValue);
    }

    [Fact]
    public async Task A_key_that_cannot_be_written_blocks_direct_mutation()
    {
        var access = new StubAccess(present: true, value: 1) { IsWritable = false };
        var operation = new UserShellPreferenceOperation(Alignment, access);

        var capability = await operation.ProbeAsync(new OperationTarget(operation.OperationId), default);

        Assert.NotEqual(SupportStatus.Supported, capability.Support);
    }

    [Fact]
    public async Task A_value_outside_the_documented_set_is_refused_at_planning_time()
    {
        var operation = new UserShellPreferenceOperation(Alignment, new StubAccess(present: true, value: 0));

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await operation.PreviewAsync(Draft(operation, "7"), default));
    }

    [Fact]
    public async Task Planning_a_value_the_target_already_holds_is_refused()
    {
        var operation = new UserShellPreferenceOperation(Alignment, new StubAccess(present: true, value: 1));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await operation.PreviewAsync(Draft(operation, "1"), default));
    }

    [Fact]
    public async Task The_plan_states_that_explorer_must_restart_for_the_change_to_show()
    {
        var operation = new UserShellPreferenceOperation(Alignment, new StubAccess(present: true, value: 1));

        var plan = await operation.PreviewAsync(Draft(operation, "0"), default);

        Assert.Equal(RestartRequirement.Explorer, plan.Restart);
        Assert.Equal(PrivilegeRequirement.StandardUser, plan.Privilege);
        Assert.Equal(RollbackCapability.Full, plan.Rollback);
        Assert.False(plan.RequiresRestorePoint);
    }

    [Fact]
    public async Task Applying_writes_the_documented_value_and_reports_the_result_fingerprint()
    {
        var access = new StubAccess(present: true, value: 1);
        var operation = new UserShellPreferenceOperation(Alignment, access);
        var plan = await operation.PreviewAsync(Draft(operation, "0"), default);

        var result = await operation.ApplyStepAsync(plan, plan.Steps[0], default);

        Assert.Equal(StepResultKind.Applied, result.Kind);
        Assert.Equal(0, access.Value);
        var verification = await operation.VerifyStepAsync(plan, plan.Steps[0], default);
        Assert.True(verification.IsVerified);
    }

    /// <summary>
    /// The case most easily got wrong: the prior state was "no value at all", so restoring it must
    /// delete the value. Writing a zero would leave the registry in a shape the user never had.
    /// </summary>
    [Fact]
    public async Task Rolling_back_to_an_absent_value_deletes_it_rather_than_writing_a_default()
    {
        var access = new StubAccess(present: false, value: null);
        var operation = new UserShellPreferenceOperation(Alignment, access);
        var plan = await operation.PreviewAsync(Draft(operation, "0"), default);

        await operation.ApplyStepAsync(plan, plan.Steps[0], default);
        Assert.True(access.IsPresent);

        var rollback = RollbackFor(plan);
        var result = await operation.RollbackStepAsync(rollback, plan.Steps[0], default);

        Assert.Equal(StepResultKind.Applied, result.Kind);
        Assert.False(access.IsPresent);
        Assert.Null(access.Value);
    }

    [Fact]
    public async Task Rollback_is_idempotent_and_writes_only_once()
    {
        var access = new StubAccess(present: true, value: 1);
        var operation = new UserShellPreferenceOperation(Alignment, access);
        var plan = await operation.PreviewAsync(Draft(operation, "0"), default);
        await operation.ApplyStepAsync(plan, plan.Steps[0], default);

        var rollback = RollbackFor(plan);
        await operation.RollbackStepAsync(rollback, plan.Steps[0], default);
        var writesAfterFirst = access.WriteCount;
        var second = await operation.RollbackStepAsync(rollback, plan.Steps[0], default);

        Assert.Equal(StepResultKind.AlreadyRestored, second.Kind);
        Assert.Equal(writesAfterFirst, access.WriteCount);
    }

    [Fact]
    public async Task External_drift_after_the_dry_run_is_refused_rather_than_overwritten()
    {
        var access = new StubAccess(present: true, value: 1);
        var operation = new UserShellPreferenceOperation(Alignment, access);
        var plan = await operation.PreviewAsync(Draft(operation, "0"), default);

        // Something else changed the value between the dry run and the apply.
        access.SetExternally(present: false, value: null);
        var result = await operation.ApplyStepAsync(plan, plan.Steps[0], default);

        Assert.Equal(StepResultKind.NotApplied, result.Kind);
        Assert.Equal(0, access.WriteCount);
    }

    [Fact]
    public async Task A_step_from_another_operation_is_rejected()
    {
        var operation = new UserShellPreferenceOperation(Alignment, new StubAccess(present: true, value: 1));
        var other = new UserShellPreferenceOperation(
            DocumentedShellValues.Find("ShowTaskViewButton"),
            new StubAccess(present: true, value: 1));
        var plan = await operation.PreviewAsync(Draft(operation, "0"), default);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await other.ApplyStepAsync(plan, plan.Steps[0], default));
    }

    private static OperationDraft Draft(UserShellPreferenceOperation operation, string proposed) => new(
        operation.OperationId,
        "winora.category.personalization",
        "Taskbar alignment",
        "Changes the documented taskbar alignment value.",
        new OperationTarget(operation.OperationId),
        new DisplayValue(ShellPreferenceValues.Kind, proposed));

    private static RollbackPlan RollbackFor(ChangePlan plan) => RollbackPlan.Create(
        Guid.NewGuid(),
        plan,
        BackupReceipt.Verified("backup", "BACKUP-DIGEST", plan.Digest, plan.SourceFingerprint, plan.SourceFingerprint),
        plan.Steps[^1].ResultFingerprint);

    private sealed class StubAccess(bool present, int? value) : IUserShellPreferenceAccess
    {
        public bool IsPresent { get; private set; } = present;

        public int? Value { get; private set; } = value;

        public bool KindMatchesDocumentation { get; init; } = true;

        public bool IsWritable { get; init; } = true;

        public int WriteCount { get; private set; }

        public void SetExternally(bool present, int? value)
        {
            IsPresent = present;
            Value = value;
        }

        public ShellPreferenceReading Read(DocumentedShellValue entry) =>
            new(
                IsKeyAccessible: true,
                IsValuePresent: IsPresent,
                Value: Value,
                IsKindAsDocumented: KindMatchesDocumentation,
                IsKeyWritable: IsWritable);

        public ShellPreferenceWriteOutcome Write(DocumentedShellValue entry, int value)
        {
            if (!entry.AllowedValues.Contains(value))
            {
                return ShellPreferenceWriteOutcome.NotWritten;
            }

            WriteCount++;
            IsPresent = true;
            Value = value;
            return ShellPreferenceWriteOutcome.Written;
        }

        public ShellPreferenceWriteOutcome Delete(DocumentedShellValue entry)
        {
            WriteCount++;
            IsPresent = false;
            Value = null;
            return ShellPreferenceWriteOutcome.Written;
        }
    }
}
