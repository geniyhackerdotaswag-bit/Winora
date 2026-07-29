using System.Security.Cryptography;
using System.Text;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.System.Safety;
using Winora.System.Windows;

namespace Winora.System.Operations;

/// <summary>
/// Direct, reversible control of one documented visual-effect preference.
/// </summary>
/// <remarks>
/// <para>
/// One instance owns exactly one setting. That is deliberate: the coordinator probes a plan with
/// <c>new OperationTarget(plan.OperationId)</c>, so an operation that aggregated several settings
/// could not answer with a fingerprint comparable to the plan's source fingerprint.
/// </para>
/// <para>
/// <b>Conditional mutation.</b> Unlike the registry, <c>SystemParametersInfoW</c> offers no
/// compare-and-swap. The protection here is the shape of the target rather than a transaction:
/// the value is a single BOOL with no partial state, Winora holds the global mutation lease, the
/// expected fingerprint is re-checked immediately before the indivisible documented call, and the
/// result is read back immediately after. A competing external writer inside that window is
/// therefore detected — as drift before the call, or as a contradicted readback after it — and
/// never silently overwritten, with the exact source value restorable from the verified backup.
/// </para>
/// <para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow
/// </para>
/// </remarks>
public sealed class VisualEffectsOperation : IOperation, IConditionalSystemMutation
{
    /// <summary>The documented reference for every plan this operation produces.</summary>
    public static readonly Uri Documentation =
        new("https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow");

    private static readonly StateFingerprint UnknownFingerprint = new("none", "unknown");

    private readonly IVisualEffectsAccess _access;
    private readonly VisualEffectSetting _setting;

    public VisualEffectsOperation(VisualEffectSetting setting, IVisualEffectsAccess access)
    {
        if (!Enum.IsDefined(setting))
        {
            throw new ArgumentOutOfRangeException(nameof(setting));
        }

        ArgumentNullException.ThrowIfNull(access);

        _setting = setting;
        _access = access;
        OperationId = IdFor(setting);
    }

    public string OperationId { get; }

    public string ConditionalMutationMechanismId => "windows.spi.single-value-verified-write";

    public static string IdFor(VisualEffectSetting setting) => setting switch
    {
        VisualEffectSetting.ClientAreaAnimation => "winora.visual-effects.client-area-animation",
        VisualEffectSetting.UiEffects => "winora.visual-effects.ui-effects",
        _ => throw new ArgumentOutOfRangeException(nameof(setting)),
    };

    /// <summary>
    /// The stable step identifier for the single step this operation plans. Step identifiers are
    /// display-safe by contract, so they cannot reuse the dotted catalog operation identifier.
    /// </summary>
    public static string StepIdFor(VisualEffectSetting setting) => setting switch
    {
        VisualEffectSetting.ClientAreaAnimation => "visual-effects-client-area-animation",
        VisualEffectSetting.UiEffects => "visual-effects-ui-effects",
        _ => throw new ArgumentOutOfRangeException(nameof(setting)),
    };

    public ValueTask<OperationCapability> ProbeAsync(
        OperationTarget target,
        CancellationToken cancellationToken)
    {
        EnsureTarget(target, nameof(target));
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(OperationCapabilityPolicy.Evaluate(Observe()));
    }

    public ValueTask<ChangePlan> PreviewAsync(OperationDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!StringComparer.Ordinal.Equals(draft.OperationId, OperationId))
        {
            throw new ArgumentException("The draft belongs to another operation.", nameof(draft));
        }

        EnsureTarget(draft.Target, nameof(draft));
        cancellationToken.ThrowIfCancellationRequested();

        if (!StringComparer.Ordinal.Equals(draft.ProposedValue.Kind, VisualEffectValues.Kind) ||
            !VisualEffectValues.TryParse(draft.ProposedValue.Text, out var proposed))
        {
            throw new ArgumentException("The proposed value is not a documented toggle value.", nameof(draft));
        }

        var observation = Observe();
        if (!observation.IsApiAvailable || !observation.IsTargetStateKnown)
        {
            throw new InvalidOperationException(
                "A dry run requires a readable current value from a documented action.");
        }

        var current = _access.Read(_setting).Value;
        if (current == proposed)
        {
            // No dead actions: a plan that changes nothing is never offered for confirmation.
            throw new InvalidOperationException("The setting already holds the proposed value.");
        }

        var source = Fingerprint(current);
        var result = Fingerprint(proposed);
        var step = new ChangeStep(
            StepIdFor(_setting),
            new OperationTarget(OperationId),
            new DisplayValue(VisualEffectValues.Kind, VisualEffectValues.For(current)),
            new DisplayValue(VisualEffectValues.Kind, VisualEffectValues.For(proposed)),
            source,
            result,
            new VerificationProbe($"{OperationId}.read", VisualEffectValues.For(proposed)));

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
            RestartRequirement.None,
            OperationCapabilityPolicy.Evaluate(observation).Support,
            source,
            Documentation,
            BackupRequirement.Required,
            requiresRestorePoint: false));
    }

    public ValueTask<StepResult> ApplyStepAsync(
        ChangePlan plan,
        ChangeStep step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureStep(step);
        cancellationToken.ThrowIfCancellationRequested();

        if (!VisualEffectValues.TryParse(step.ProposedValue.Text, out var proposed))
        {
            return ValueTask.FromResult(
                StepResult.NotApplied("The step does not carry a documented toggle value."));
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
        var reading = _access.Read(_setting);
        if (!reading.IsActionAvailable || !reading.IsReadable)
        {
            return ValueTask.FromResult(
                VerificationResult.Failed(UnknownFingerprint, "The applied value could not be read back."));
        }

        var observed = Fingerprint(reading.Value);
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

        if (!VisualEffectValues.TryParse(step.CurrentValue.Text, out var source))
        {
            return ValueTask.FromResult(
                StepResult.NotApplied("The step does not carry a documented source value."));
        }

        var reading = _access.Read(_setting);
        if (!reading.IsActionAvailable || !reading.IsReadable)
        {
            return ValueTask.FromResult(
                StepResult.FailedOutcomeUnknown("The current value could not be read before rollback."));
        }

        var observed = Fingerprint(reading.Value);

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
    /// issue the indivisible documented call, then read back and compare.
    /// </summary>
    private StepResult Mutate(
        StateFingerprint expected,
        StateFingerprint intended,
        bool value)
    {
        var before = _access.Read(_setting);
        if (!before.IsActionAvailable || !before.IsReadable)
        {
            return StepResult.NotApplied("The current value could not be read before the conditional write.");
        }

        if (Fingerprint(before.Value) != expected)
        {
            return StepResult.NotApplied("The value changed after the dry run and was not overwritten.");
        }

        var outcome = _access.Write(_setting, value);
        if (outcome == VisualEffectWriteOutcome.OutcomeUnknown)
        {
            return StepResult.FailedOutcomeUnknown("The documented call returned an unattributable failure.");
        }

        var after = _access.Read(_setting);
        if (!after.IsActionAvailable || !after.IsReadable)
        {
            return StepResult.FailedOutcomeUnknown("The value could not be read back after the write.");
        }

        var observed = Fingerprint(after.Value);
        if (outcome == VisualEffectWriteOutcome.Written)
        {
            return observed == intended
                ? StepResult.Applied(observed)
                : StepResult.FailedOutcomeUnknown("The write reported success but the readback disagrees.");
        }

        // The call reported failure. Trust the readback rather than the return value.
        return observed == expected
            ? StepResult.NotApplied("The documented call failed and the value is unchanged.")
            : StepResult.FailedOutcomeUnknown("The write reported failure but the value changed.");
    }

    private CapabilityObservation Observe()
    {
        var reading = _access.Read(_setting);
        var isUsable = reading.IsActionAvailable && reading.IsReadable;

        return new CapabilityObservation(
            IsApiAvailable: reading.IsActionAvailable,
            IsTargetStateKnown: reading.IsReadable,
            IsWritable: reading.IsActionAvailable,
            IsRemoteTarget: false,
            IsProtectedTarget: false,
            IsBackupAvailable: isUsable,
            IsVerificationAvailable: isUsable,
            IsRollbackAvailable: isUsable,
            IsConditionalMutationAvailable: isUsable,
            RequiredPrivilege: PrivilegeRequirement.StandardUser,
            IsElevationSupportedForAccount: true,
            CurrentFingerprint: reading.IsReadable ? Fingerprint(reading.Value) : UnknownFingerprint);
    }

    private StateFingerprint Fingerprint(bool value) => new(
        "SHA-256",
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{OperationId}={VisualEffectValues.For(value)}"))));

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
    }
}
