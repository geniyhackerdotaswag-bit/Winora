using Winora.Core.Changes;

namespace Winora.Core.Contracts;

public sealed record OperationDraft(
    string OperationId,
    string Category,
    string Title,
    string Summary,
    OperationTarget Target,
    DisplayValue ProposedValue);

public enum StepResultKind
{
    Applied,
    NotApplied,
    FailedOutcomeUnknown,
    AlreadyRestored,
}

public sealed record StepResult(
    StepResultKind Kind,
    StateFingerprint? ObservedFingerprint,
    string? Detail)
{
    public static StepResult Applied(StateFingerprint fingerprint) =>
        new(StepResultKind.Applied, fingerprint, null);

    public static StepResult NotApplied(string detail) =>
        new(StepResultKind.NotApplied, null, detail);

    public static StepResult FailedOutcomeUnknown(string detail) =>
        new(StepResultKind.FailedOutcomeUnknown, null, detail);

    public static StepResult AlreadyRestored(StateFingerprint fingerprint) =>
        new(StepResultKind.AlreadyRestored, fingerprint, null);
}

public sealed record VerificationResult(
    bool IsVerified,
    StateFingerprint ObservedFingerprint,
    string? Detail)
{
    public static VerificationResult Passed(StateFingerprint fingerprint) => new(true, fingerprint, null);

    public static VerificationResult Failed(StateFingerprint fingerprint, string detail) =>
        new(false, fingerprint, detail);
}

public interface IOperation
{
    string OperationId { get; }

    ValueTask<OperationCapability> ProbeAsync(
        OperationTarget target,
        CancellationToken cancellationToken);

    ValueTask<ChangePlan> PreviewAsync(OperationDraft draft, CancellationToken cancellationToken);

    ValueTask<StepResult> ApplyStepAsync(
        ChangePlan plan,
        ChangeStep step,
        CancellationToken cancellationToken);

    ValueTask<VerificationResult> VerifyStepAsync(
        ChangePlan plan,
        ChangeStep step,
        CancellationToken cancellationToken);

    ValueTask<StepResult> RollbackStepAsync(
        RollbackPlan plan,
        ChangeStep step,
        CancellationToken cancellationToken);
}
