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
        var live = await LiveFingerprintAsync(plan.ChangePlan, cancellationToken).ConfigureAwait(false);

        // A pre-rollback checkpoint records the state rollback is about to leave behind, which is the
        // applied value, not the original one.
        var artifacts = plan.ChangePlan.Steps
            .Select(static step => BackupArtifact.Create(
                step.StepId,
                step.ProposedValue.Kind,
                Encoding.UTF8.GetBytes(step.ProposedValue.Text)))
            .ToArray();

        return BackupCapture.ForRecoveryCheckpoint(plan.ChangePlan.SourceFingerprint, live, artifacts);
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
