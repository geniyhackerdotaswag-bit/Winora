using System.Text;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.System.Backups;
using Winora.System.Operations;
using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Backups;

/// <summary>
/// The capture provider decides which state a backup describes. Getting that wrong does not fail
/// loudly at the call site: the coordinator compares the receipt against the plan and refuses, so a
/// mistake here surfaces only as a rollback that will not run. These tests pin the contract.
/// </summary>
public sealed class OperationBackupCaptureProviderTests
{
    private const string ClientAreaAnimation = "winora.visual-effects.client-area-animation";

    [Fact]
    public async Task An_operation_capture_describes_the_state_the_apply_will_overwrite()
    {
        var access = new StubAccess(currentValue: false);
        var operation = new VisualEffectsOperation(VisualEffectSetting.ClientAreaAnimation, access);
        var provider = new OperationBackupCaptureProvider(new CompositeOperationCatalog([operation], []));
        var plan = await PlanAsync(operation, proposed: true);

        var capture = await provider.CaptureOperationAsync(plan, CancellationToken.None);

        Assert.Equal(BackupCaptureKind.Operation, capture.Kind);
        Assert.Equal(plan.SourceFingerprint, capture.CapturedSourceFingerprint);
        Assert.Equal(plan.SourceFingerprint, capture.LiveSourceFingerprint);
        Assert.Equal("off", Encoding.UTF8.GetString(capture.Artifacts.Single().Content.ToArray()));
    }

    /// <summary>
    /// The regression this file exists for. A pre-rollback checkpoint records the applied value,
    /// because that is what rollback is about to overwrite. Reporting the original source instead
    /// makes the coordinator reject the checkpoint and the rollback fails with the machine still
    /// changed — exactly the situation a rollback is supposed to prevent.
    /// </summary>
    [Fact]
    public async Task A_recovery_checkpoint_describes_the_applied_state_not_the_original_one()
    {
        var access = new StubAccess(currentValue: false);
        var operation = new VisualEffectsOperation(VisualEffectSetting.ClientAreaAnimation, access);
        var provider = new OperationBackupCaptureProvider(new CompositeOperationCatalog([operation], []));
        var plan = await PlanAsync(operation, proposed: true);

        // The apply already happened, so the live system now holds the proposed value.
        access.CurrentValue = true;
        var rollback = RollbackPlan.Create(
            Guid.NewGuid(),
            plan,
            BackupReceipt.Verified("backup", "BACKUP-DIGEST", plan.Digest, plan.SourceFingerprint, plan.SourceFingerprint),
            plan.Steps[^1].ResultFingerprint);

        var capture = await provider.CaptureRecoveryCheckpointAsync(rollback, CancellationToken.None);

        Assert.Equal(BackupCaptureKind.RecoveryCheckpoint, capture.Kind);
        Assert.Equal(rollback.AppliedFingerprint, capture.CapturedSourceFingerprint);
        Assert.Equal(rollback.AppliedFingerprint, capture.LiveSourceFingerprint);
        Assert.Equal("on", Encoding.UTF8.GetString(capture.Artifacts.Single().Content.ToArray()));
    }

    [Fact]
    public async Task Drift_between_apply_and_rollback_makes_the_live_fingerprint_disagree()
    {
        var access = new StubAccess(currentValue: false);
        var operation = new VisualEffectsOperation(VisualEffectSetting.ClientAreaAnimation, access);
        var provider = new OperationBackupCaptureProvider(new CompositeOperationCatalog([operation], []));
        var plan = await PlanAsync(operation, proposed: true);

        // Something outside Winora put the value back before rollback ran.
        var rollback = RollbackPlan.Create(
            Guid.NewGuid(),
            plan,
            BackupReceipt.Verified("backup", "BACKUP-DIGEST", plan.Digest, plan.SourceFingerprint, plan.SourceFingerprint),
            plan.Steps[^1].ResultFingerprint);

        var capture = await provider.CaptureRecoveryCheckpointAsync(rollback, CancellationToken.None);

        Assert.NotEqual(capture.CapturedSourceFingerprint, capture.LiveSourceFingerprint);
    }

    [Fact]
    public async Task A_plan_from_an_unregistered_operation_is_refused_rather_than_captured_blindly()
    {
        var access = new StubAccess(currentValue: false);
        var operation = new VisualEffectsOperation(VisualEffectSetting.ClientAreaAnimation, access);
        var plan = await PlanAsync(operation, proposed: true);
        var provider = new OperationBackupCaptureProvider(new CompositeOperationCatalog([], []));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.CaptureOperationAsync(plan, CancellationToken.None));
    }

    private static async Task<ChangePlan> PlanAsync(VisualEffectsOperation operation, bool proposed) =>
        await operation.PreviewAsync(
            new OperationDraft(
                operation.OperationId,
                "winora.category.personalization",
                "Client area animation",
                "Turns the documented client-area animation preference on or off.",
                new OperationTarget(operation.OperationId),
                new DisplayValue(VisualEffectValues.Kind, VisualEffectValues.For(proposed))),
            CancellationToken.None);

    private sealed class StubAccess(bool currentValue) : IVisualEffectsAccess
    {
        public bool CurrentValue { get; set; } = currentValue;

        public VisualEffectReading Read(VisualEffectSetting setting) =>
            new(IsActionAvailable: true, IsReadable: true, Value: CurrentValue);

        public VisualEffectWriteOutcome Write(VisualEffectSetting setting, bool value)
        {
            CurrentValue = value;
            return VisualEffectWriteOutcome.Written;
        }
    }
}
