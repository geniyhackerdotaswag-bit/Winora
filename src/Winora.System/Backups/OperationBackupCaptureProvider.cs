using System.Text;
using Winora.Core.Changes;
using Winora.Core.Contracts;

namespace Winora.System.Backups;

/// <summary>
/// Captures the exact source state a plan intends to overwrite, by asking the owning operation to
/// re-probe the live system. The live fingerprint comes from that fresh probe, never from the plan,
/// so the coordinator can detect drift between planning and backup.
/// </summary>
public sealed class OperationBackupCaptureProvider : IBackupCaptureProvider
{
    private readonly IReadOnlyDictionary<string, IOperation> _operations;

    public OperationBackupCaptureProvider(IEnumerable<IOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _operations = operations.ToDictionary(
            static operation => operation.OperationId,
            StringComparer.Ordinal);
    }

    public async ValueTask<BackupCapture> CaptureOperationAsync(
        ChangePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var live = await LiveFingerprintAsync(plan, cancellationToken).ConfigureAwait(false);
        return BackupCapture.ForOperation(plan.SourceFingerprint, live, ArtifactsFor(plan));
    }

    public async ValueTask<BackupCapture> CaptureRecoveryCheckpointAsync(
        RollbackPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // A pre-rollback checkpoint records the state rollback is about to overwrite, which is the
        // applied value, never the original one. The coordinator enforces this: it requires both
        // fingerprints on the receipt to equal the plan's applied fingerprint, so reporting the
        // original source here fails the rollback instead of silently checkpointing the wrong state.
        var live = await LiveFingerprintAsync(plan.ChangePlan, cancellationToken).ConfigureAwait(false);

        var artifacts = plan.ChangePlan.Steps
            .Select(static step => BackupArtifact.Create(
                step.StepId,
                step.ProposedValue.Kind,
                Encoding.UTF8.GetBytes(step.ProposedValue.Text)))
            .ToArray();

        // Passing the fresh probe as the live fingerprint is what makes drift between apply and
        // rollback surface as a refusal rather than an overwrite.
        return BackupCapture.ForRecoveryCheckpoint(plan.AppliedFingerprint, live, artifacts);
    }

    private static IReadOnlyList<BackupArtifact> ArtifactsFor(ChangePlan plan) =>
        plan.Steps
            .Select(static step => BackupArtifact.Create(
                step.StepId,
                step.CurrentValue.Kind,
                Encoding.UTF8.GetBytes(step.CurrentValue.Text)))
            .ToArray();

    private async ValueTask<StateFingerprint> LiveFingerprintAsync(
        ChangePlan plan,
        CancellationToken cancellationToken)
    {
        if (!_operations.TryGetValue(plan.OperationId, out var operation))
        {
            throw new InvalidOperationException(
                $"No registered operation owns '{plan.OperationId}', so its source state cannot be captured.");
        }

        var capability = await operation
            .ProbeAsync(new OperationTarget(plan.OperationId), cancellationToken)
            .ConfigureAwait(false);

        return capability.CurrentFingerprint;
    }
}
