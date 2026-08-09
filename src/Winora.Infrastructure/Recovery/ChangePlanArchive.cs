using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Infrastructure.Paths;

namespace Winora.Infrastructure.Recovery;

/// <summary>
/// Keeps the canonical <see cref="ChangePlan"/> next to the operation it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Recovery needs the original plan: <c>RollbackAsync</c> takes a <see cref="RollbackPlan"/> built
/// from it, and the coordinator matches the plan's digest against the durable boundary. The journal
/// records the digest, the step order and the recovery descriptors, but not the plan itself, so an
/// operation that stopped halfway could not be rolled back at all — the one thing that would have
/// cleared it.
/// </para>
/// <para>
/// Storing the plan is preferred over rebuilding it from those fragments. A reconstruction that
/// differs in any field produces a different digest, and the coordinator would then refuse the
/// rollback for what looks like drift; keeping the original removes that whole class of failure.
/// </para>
/// <para>
/// The archive is written before the first mutation and read only during recovery. A missing file
/// is a normal answer, not an error: operations planned before this existed have none.
/// </para>
/// </remarks>
public interface IChangePlanArchive
{
    Task SaveAsync(ChangePlan plan, CancellationToken cancellationToken);

    Task<ChangePlan?> TryLoadAsync(Guid planId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class ChangePlanArchive : IChangePlanArchive
{
    private const string FileName = "plan.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly WinoraDataPaths _paths;

    public ChangePlanArchive(WinoraDataPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task SaveAsync(ChangePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var file = FileFor(plan.PlanId);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        // Written to a sibling and moved into place, so a crash mid-write cannot leave a truncated
        // plan that would later be read back as if it were the real one.
        var temporary = file + ".writing";
        var payload = JsonSerializer.SerializeToUtf8Bytes(PlanRecord.From(plan), SerializerOptions);
        await File.WriteAllBytesAsync(temporary, payload, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, file, overwrite: true);
    }

    public async Task<ChangePlan?> TryLoadAsync(Guid planId, CancellationToken cancellationToken)
    {
        var file = FileFor(planId);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var payload = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            var record = JsonSerializer.Deserialize<PlanRecord>(payload, SerializerOptions);
            return record?.ToPlan();
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
        {
            // An unreadable archive is treated as absent. Recovery then reports that it cannot
            // proceed, which is honest; guessing at a plan would be far worse.
            return null;
        }
    }

    private string FileFor(Guid planId) =>
        Path.Combine(_paths.OperationsDirectory, planId.ToString("N"), FileName);

    private sealed record FingerprintRecord(string Algorithm, string Value)
    {
        public static FingerprintRecord From(StateFingerprint fingerprint) =>
            new(fingerprint.Algorithm, fingerprint.Value);

        public StateFingerprint ToFingerprint() => new(Algorithm, Value);
    }

    private sealed record ValueRecord(string Kind, string Text)
    {
        public static ValueRecord From(DisplayValue value) => new(value.Kind, value.Text);

        public DisplayValue ToValue() => new(Kind, Text);
    }

    private sealed record StepRecord(
        string StepId,
        string TargetId,
        ValueRecord CurrentValue,
        ValueRecord ProposedValue,
        FingerprintRecord SourceFingerprint,
        FingerprintRecord ResultFingerprint,
        string ProbeId,
        string ProbeExpectedResult)
    {
        public static StepRecord From(ChangeStep step) =>
            new(
                step.StepId,
                step.Target.TargetId,
                ValueRecord.From(step.CurrentValue),
                ValueRecord.From(step.ProposedValue),
                FingerprintRecord.From(step.SourceFingerprint),
                FingerprintRecord.From(step.ResultFingerprint),
                step.Verification.ProbeId,
                step.Verification.ExpectedResult);

        public ChangeStep ToStep() =>
            new(
                StepId,
                new OperationTarget(TargetId),
                CurrentValue.ToValue(),
                ProposedValue.ToValue(),
                SourceFingerprint.ToFingerprint(),
                ResultFingerprint.ToFingerprint(),
                new VerificationProbe(ProbeId, ProbeExpectedResult));
    }

    private sealed record PlanRecord(
        Guid PlanId,
        string OperationId,
        string Category,
        string Title,
        string Summary,
        IReadOnlyList<StepRecord> Steps,
        RiskLevel Risk,
        PrivilegeRequirement Privilege,
        RollbackCapability Rollback,
        RestartRequirement Restart,
        SupportStatus Support,
        FingerprintRecord SourceFingerprint,
        string Documentation,
        BackupRequirement Backup,
        bool RequiresRestorePoint,
        ChangePlanKind Kind)
    {
        public static PlanRecord From(ChangePlan plan) =>
            new(
                plan.PlanId,
                plan.OperationId,
                plan.Category,
                plan.Title,
                plan.Summary,
                plan.Steps.Select(StepRecord.From).ToArray(),
                plan.Risk,
                plan.Privilege,
                plan.Rollback,
                plan.Restart,
                plan.Support,
                FingerprintRecord.From(plan.SourceFingerprint),
                plan.Documentation.ToString(),
                plan.Backup,
                plan.RequiresRestorePoint,
                plan.Kind);

        public ChangePlan ToPlan() =>
            ChangePlan.Create(
                PlanId,
                OperationId,
                Category,
                Title,
                Summary,
                Steps.Select(static step => step.ToStep()).ToArray(),
                Risk,
                Privilege,
                Rollback,
                Restart,
                Support,
                SourceFingerprint.ToFingerprint(),
                new Uri(Documentation),
                Backup,
                RequiresRestorePoint,
                Kind);
    }
}
