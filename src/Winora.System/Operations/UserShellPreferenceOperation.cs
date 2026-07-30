using System.Security.Cryptography;
using System.Text;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.System.Safety;
using Winora.System.Windows;

namespace Winora.System.Operations;

/// <summary>
/// One documented per-user Explorer preference. Reads, plans, applies conditionally, verifies with
/// an independent read, and restores the exact prior state — including restoring absence by
/// deleting the value rather than writing a default Winora chose.
/// </summary>
/// <remarks>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/apps/develop/settings/settings-windows-11
/// </remarks>
public sealed class UserShellPreferenceOperation : IOperation, IConditionalSystemMutation
{
    private static readonly StateFingerprint UnknownFingerprint = new("SHA-256", new string('0', 64));

    private readonly DocumentedShellValue _entry;
    private readonly IUserShellPreferenceAccess _access;

    public UserShellPreferenceOperation(DocumentedShellValue entry, IUserShellPreferenceAccess access)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        OperationId = entry.OperationId;
    }

    public string OperationId { get; }

    public string ConditionalMutationMechanismId => "windows.registry.single-value-verified-write";

    public ValueTask<OperationCapability> ProbeAsync(OperationTarget target, CancellationToken cancellationToken)
    {
        EnsureTarget(target, nameof(target));
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(OperationCapabilityPolicy.Evaluate(Observe()));
    }

    public ValueTask<ChangePlan> PreviewAsync(OperationDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        EnsureTarget(draft.Target, nameof(draft));
        cancellationToken.ThrowIfCancellationRequested();

        if (!StringComparer.Ordinal.Equals(draft.ProposedValue.Kind, ShellPreferenceValues.Kind) ||
            !ShellPreferenceValues.TryParse(draft.ProposedValue.Text, out var proposed))
        {
            throw new ArgumentException("The proposed value is not a documented shell value.", nameof(draft));
        }

        if (proposed is { } number && !_entry.AllowedValues.Contains(number))
        {
            throw new ArgumentException("The proposed value is outside the documented set.", nameof(draft));
        }

        var reading = _access.Read(_entry);
        if (!reading.IsUsable)
        {
            throw new InvalidOperationException(
                "A dry run requires a readable value of the documented kind.");
        }

        var current = Current(reading);
        if (current == proposed)
        {
            // No dead actions: a plan that changes nothing is never offered for confirmation.
            throw new InvalidOperationException("The setting already holds the proposed value.");
        }

        var source = Fingerprint(current);
        var result = Fingerprint(proposed);
        var step = new ChangeStep(
            _entry.StepId,
            new OperationTarget(OperationId),
            new DisplayValue(ShellPreferenceValues.Kind, ShellPreferenceValues.For(current)),
            new DisplayValue(ShellPreferenceValues.Kind, ShellPreferenceValues.For(proposed)),
            source,
            result,
            new VerificationProbe($"{OperationId}.read", ShellPreferenceValues.For(proposed)));

        return ValueTask.FromResult(ChangePlan.Create(
            Guid.NewGuid(),
            OperationId,
            draft.Category,
            draft.Title,
            draft.Summary,
            [step],
            RiskLevel.Low,
            PrivilegeRequirement.StandardUser,
            RollbackCapability.Full,
            _entry.Restart,
            OperationCapabilityPolicy.Evaluate(Observe()).Support,
            source,
            _entry.Documentation,
            BackupRequirement.Required,
            requiresRestorePoint: false));
    }

    public ValueTask<StepResult> ApplyStepAsync(ChangePlan plan, ChangeStep step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureStep(step);
        cancellationToken.ThrowIfCancellationRequested();

        if (!ShellPreferenceValues.TryParse(step.ProposedValue.Text, out var proposed))
        {
            return ValueTask.FromResult(StepResult.NotApplied("The step does not carry a documented value."));
        }

        return ValueTask.FromResult(Mutate(step.SourceFingerprint, step.ResultFingerprint, proposed));
    }

    public ValueTask<VerificationResult> VerifyStepAsync(
        ChangePlan plan,
        ChangeStep step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureStep(step);
        cancellationToken.ThrowIfCancellationRequested();

        // An independent read, not a cached echo of the value that was just written.
        var reading = _access.Read(_entry);
        if (!reading.IsUsable)
        {
            return ValueTask.FromResult(
                VerificationResult.Failed(UnknownFingerprint, "The applied value could not be read back."));
        }

        var observed = Fingerprint(Current(reading));
        return ValueTask.FromResult(observed == step.ResultFingerprint
            ? VerificationResult.Passed(observed)
            : VerificationResult.Failed(observed, "The observed value does not match the confirmed result."));
    }

    public ValueTask<StepResult> RollbackStepAsync(
        RollbackPlan plan,
        ChangeStep step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureStep(step);
        cancellationToken.ThrowIfCancellationRequested();

        if (!ShellPreferenceValues.TryParse(step.CurrentValue.Text, out var source))
        {
            return ValueTask.FromResult(StepResult.NotApplied("The step does not carry a documented source value."));
        }

        var reading = _access.Read(_entry);
        if (!reading.IsUsable)
        {
            return ValueTask.FromResult(
                StepResult.FailedOutcomeUnknown("The current value could not be read before rollback."));
        }

        var observed = Fingerprint(Current(reading));

        // Idempotence: a repeated rollback observes the source state and writes nothing.
        if (observed == step.SourceFingerprint)
        {
            return ValueTask.FromResult(StepResult.AlreadyRestored(observed));
        }

        if (observed != step.ResultFingerprint)
        {
            return ValueTask.FromResult(StepResult.NotApplied(
                "The current value is neither the applied nor the source value; rollback needs conflict review."));
        }

        return ValueTask.FromResult(Mutate(step.ResultFingerprint, step.SourceFingerprint, source));
    }

    /// <summary>
    /// The conditional sequence shared by apply and rollback: re-check the expected fingerprint,
    /// issue the documented write, then read back and compare. Restoring absence deletes the value
    /// rather than writing a default.
    /// </summary>
    private StepResult Mutate(StateFingerprint expected, StateFingerprint intended, int? value)
    {
        var before = _access.Read(_entry);
        if (!before.IsUsable)
        {
            return StepResult.NotApplied("The current value could not be read before the conditional write.");
        }

        if (Fingerprint(Current(before)) != expected)
        {
            return StepResult.NotApplied("The value changed after the dry run and was not overwritten.");
        }

        var outcome = value is { } number ? _access.Write(_entry, number) : _access.Delete(_entry);
        if (outcome == ShellPreferenceWriteOutcome.OutcomeUnknown)
        {
            return StepResult.FailedOutcomeUnknown("The documented write returned an unattributable failure.");
        }

        var after = _access.Read(_entry);
        if (!after.IsUsable)
        {
            return StepResult.FailedOutcomeUnknown("The value could not be read back after the write.");
        }

        var observed = Fingerprint(Current(after));
        if (outcome == ShellPreferenceWriteOutcome.Written)
        {
            return observed == intended
                ? StepResult.Applied(observed)
                : StepResult.FailedOutcomeUnknown("The write reported success but the value does not match.");
        }

        return observed == expected
            ? StepResult.NotApplied("The documented write was refused and the value is unchanged.")
            : StepResult.FailedOutcomeUnknown("The write was refused but the value no longer matches the source.");
    }

    private CapabilityObservation Observe()
    {
        var reading = _access.Read(_entry);
        var isUsable = reading.IsUsable;

        return new CapabilityObservation(
            IsApiAvailable: reading.IsKeyAccessible,
            IsTargetStateKnown: isUsable,
            IsWritable: reading.IsKeyWritable,
            IsRemoteTarget: false,
            IsProtectedTarget: false,
            IsBackupAvailable: isUsable,
            IsVerificationAvailable: isUsable,
            IsRollbackAvailable: isUsable,
            IsConditionalMutationAvailable: isUsable,
            RequiredPrivilege: PrivilegeRequirement.StandardUser,
            IsElevationSupportedForAccount: true,
            CurrentFingerprint: isUsable ? Fingerprint(Current(reading)) : UnknownFingerprint,
            CurrentValue: isUsable
                ? new DisplayValue(ShellPreferenceValues.Kind, ShellPreferenceValues.For(reading))
                : null);
    }

    private static int? Current(ShellPreferenceReading reading) =>
        reading.IsValuePresent ? reading.Value : null;

    private StateFingerprint Fingerprint(int? value) => new(
        "SHA-256",
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{OperationId}={ShellPreferenceValues.For(value)}"))));

    private void EnsureTarget(OperationTarget target, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(target, parameterName);
        if (!StringComparer.Ordinal.Equals(target.TargetId, OperationId))
        {
            throw new ArgumentException("The target belongs to another operation.", parameterName);
        }
    }

    private void EnsureStep(ChangeStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        EnsureTarget(step.Target, nameof(step));
        if (!StringComparer.Ordinal.Equals(step.StepId, _entry.StepId))
        {
            throw new ArgumentException("The step belongs to another operation.", nameof(step));
        }
    }
}
