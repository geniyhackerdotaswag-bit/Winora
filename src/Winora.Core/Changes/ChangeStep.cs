namespace Winora.Core.Changes;

public sealed record ChangeStep(
    string StepId,
    OperationTarget Target,
    DisplayValue CurrentValue,
    DisplayValue ProposedValue,
    StateFingerprint SourceFingerprint,
    StateFingerprint ResultFingerprint,
    VerificationProbe Verification);
