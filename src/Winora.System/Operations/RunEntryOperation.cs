using System.Security.Cryptography;
using System.Text;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.System.Safety;
using Winora.System.Windows;

namespace Winora.System.Operations;

/// <summary>The stable value vocabulary for a startup entry.</summary>
public static class RunEntryValues
{
    public const string Kind = "winora.value.startup-state";

    public const string Enabled = "enabled";

    public const string Disabled = "disabled";

    public static string For(RunEntryState state) => state switch
    {
        RunEntryState.Enabled => Enabled,
        RunEntryState.Disabled => Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "An absent entry has no value."),
    };

    public static bool TryParse(string? text, out RunEntryState state)
    {
        switch (text)
        {
            case Enabled:
                state = RunEntryState.Enabled;
                return true;
            case Disabled:
                state = RunEntryState.Disabled;
                return true;
            default:
                state = RunEntryState.Absent;
                return false;
        }
    }
}

/// <summary>
/// Enables or disables one documented startup entry by moving its value between the Run key and
/// Winora's holding key.
/// </summary>
public sealed class RunEntryOperation : IOperation, IConditionalSystemMutation
{
    internal const string IdPrefix = "winora.startup.run.n";

    private static readonly StateFingerprint UnknownFingerprint = new("SHA-256", new string('0', 64));

    private static readonly Uri Documentation =
        new("https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys");

    private readonly string _entryName;
    private readonly IRunEntryStore _store;

    public RunEntryOperation(string entryName, IRunEntryStore store)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryName);
        _entryName = entryName;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        OperationId = IdFor(entryName);
    }

    public string OperationId { get; }

    public string ConditionalMutationMechanismId => "windows.registry.entry-move-verified";

    public string EntryName => _entryName;

    /// <summary>
    /// A catalog identifier caps at 96 lowercase characters, and real entry names reach 56 bytes, so
    /// the name cannot be encoded in the id. It is hashed instead, and the factory finds the entry
    /// again by hashing the names it can see. Disabling moves the value rather than deleting it
    /// precisely so that the name remains visible to that scan.
    /// </summary>
    public static string IdFor(string entryName)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryName);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(entryName));
        return IdPrefix + Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    public static string StepIdFor(string entryName) =>
        "startup-" + IdFor(entryName)[IdPrefix.Length..];

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

        if (!StringComparer.Ordinal.Equals(draft.ProposedValue.Kind, RunEntryValues.Kind) ||
            !RunEntryValues.TryParse(draft.ProposedValue.Text, out var proposed))
        {
            throw new ArgumentException("The proposed value is not a documented startup state.", nameof(draft));
        }

        var reading = _store.Read(_entryName);
        if (reading.State == RunEntryState.Absent || !reading.IsWritable)
        {
            throw new InvalidOperationException("A dry run requires an existing, writable startup entry.");
        }

        if (reading.State == proposed)
        {
            // No dead actions: a plan that changes nothing is never offered for confirmation.
            throw new InvalidOperationException("The entry already holds the proposed state.");
        }

        var source = Fingerprint(reading.State);
        var result = Fingerprint(proposed);
        var step = new ChangeStep(
            StepIdFor(_entryName),
            new OperationTarget(OperationId),
            new DisplayValue(RunEntryValues.Kind, RunEntryValues.For(reading.State)),
            new DisplayValue(RunEntryValues.Kind, RunEntryValues.For(proposed)),
            source,
            result,
            new VerificationProbe($"{OperationId}.read", RunEntryValues.For(proposed)));

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
            RestartRequirement.SignOut,
            OperationCapabilityPolicy.Evaluate(Observe()).Support,
            source,
            Documentation,
            BackupRequirement.Required,
            requiresRestorePoint: false));
    }

    public ValueTask<StepResult> ApplyStepAsync(ChangePlan plan, ChangeStep step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureStep(step);
        cancellationToken.ThrowIfCancellationRequested();

        return RunEntryValues.TryParse(step.ProposedValue.Text, out var proposed)
            ? ValueTask.FromResult(Mutate(step.SourceFingerprint, step.ResultFingerprint, proposed))
            : ValueTask.FromResult(StepResult.NotApplied("The step does not carry a documented state."));
    }

    public ValueTask<VerificationResult> VerifyStepAsync(
        ChangePlan plan,
        ChangeStep step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureStep(step);
        cancellationToken.ThrowIfCancellationRequested();

        // An independent read, not a cached echo of the move that was just performed.
        var reading = _store.Read(_entryName);
        if (reading.State == RunEntryState.Absent)
        {
            return ValueTask.FromResult(
                VerificationResult.Failed(UnknownFingerprint, "The entry could not be found after the move."));
        }

        var observed = Fingerprint(reading.State);
        return ValueTask.FromResult(observed == step.ResultFingerprint
            ? VerificationResult.Passed(observed)
            : VerificationResult.Failed(observed, "The observed state does not match the confirmed result."));
    }

    public ValueTask<StepResult> RollbackStepAsync(
        RollbackPlan plan,
        ChangeStep step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureStep(step);
        cancellationToken.ThrowIfCancellationRequested();

        if (!RunEntryValues.TryParse(step.CurrentValue.Text, out var source))
        {
            return ValueTask.FromResult(StepResult.NotApplied("The step does not carry a documented source state."));
        }

        var reading = _store.Read(_entryName);
        if (reading.State == RunEntryState.Absent)
        {
            return ValueTask.FromResult(
                StepResult.FailedOutcomeUnknown("The entry could not be found before rollback."));
        }

        var observed = Fingerprint(reading.State);
        if (observed == step.SourceFingerprint)
        {
            return ValueTask.FromResult(StepResult.AlreadyRestored(observed));
        }

        if (observed != step.ResultFingerprint)
        {
            return ValueTask.FromResult(StepResult.NotApplied(
                "The current state is neither the applied nor the source state; rollback needs conflict review."));
        }

        return ValueTask.FromResult(Mutate(step.ResultFingerprint, step.SourceFingerprint, source));
    }

    private StepResult Mutate(StateFingerprint expected, StateFingerprint intended, RunEntryState target)
    {
        var before = _store.Read(_entryName);
        if (before.State == RunEntryState.Absent)
        {
            return StepResult.NotApplied("The entry could not be read before the conditional move.");
        }

        if (Fingerprint(before.State) != expected)
        {
            return StepResult.NotApplied("The entry changed after the dry run and was not overwritten.");
        }

        var outcome = target == RunEntryState.Enabled
            ? _store.Enable(_entryName)
            : _store.Disable(_entryName);

        if (outcome == RunEntryWriteOutcome.OutcomeUnknown)
        {
            return StepResult.FailedOutcomeUnknown("The move returned an unattributable failure.");
        }

        var after = _store.Read(_entryName);
        if (after.State == RunEntryState.Absent)
        {
            return StepResult.FailedOutcomeUnknown("The entry could not be read back after the move.");
        }

        var observed = Fingerprint(after.State);
        if (outcome == RunEntryWriteOutcome.Written)
        {
            return observed == intended
                ? StepResult.Applied(observed)
                : StepResult.FailedOutcomeUnknown("The move reported success but the state does not match.");
        }

        return observed == expected
            ? StepResult.NotApplied("The move was refused and the entry is unchanged.")
            : StepResult.FailedOutcomeUnknown("The move was refused but the state no longer matches the source.");
    }

    private CapabilityObservation Observe()
    {
        var reading = _store.Read(_entryName);
        var isKnown = reading.State != RunEntryState.Absent;
        var isUsable = isKnown && reading.IsWritable;

        return new CapabilityObservation(
            IsApiAvailable: true,
            IsTargetStateKnown: isKnown,
            IsWritable: reading.IsWritable,
            IsRemoteTarget: false,
            IsProtectedTarget: false,
            IsBackupAvailable: isUsable,
            IsVerificationAvailable: isUsable,
            IsRollbackAvailable: isUsable,
            IsConditionalMutationAvailable: isUsable,
            RequiredPrivilege: PrivilegeRequirement.StandardUser,
            IsElevationSupportedForAccount: true,
            CurrentFingerprint: isKnown ? Fingerprint(reading.State) : UnknownFingerprint,
            CurrentValue: isKnown
                ? new DisplayValue(RunEntryValues.Kind, RunEntryValues.For(reading.State))
                : null);
    }

    private StateFingerprint Fingerprint(RunEntryState state) => new(
        "SHA-256",
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{OperationId}={RunEntryValues.For(state)}"))));

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
        if (!StringComparer.Ordinal.Equals(step.StepId, StepIdFor(_entryName)))
        {
            throw new ArgumentException("The step belongs to another operation.", nameof(step));
        }
    }
}

/// <summary>
/// Rebuilds a startup operation from its identifier by hashing the names Winora can currently see
/// in either the Run key or its own holding key. This is why disabling moves the value instead of
/// deleting it: a deleted entry would leave nothing to match against in a fresh process.
/// </summary>
public sealed class RunEntryOperationFactory : IOperationFactory
{
    private readonly IRunEntryStore _store;

    public RunEntryOperationFactory(IRunEntryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public bool TryCreate(string operationId, out IOperation? operation)
    {
        operation = null;
        if (string.IsNullOrWhiteSpace(operationId) ||
            !operationId.StartsWith(RunEntryOperation.IdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var name in _store.EnabledNames().Concat(_store.DisabledNames()))
        {
            if (StringComparer.Ordinal.Equals(RunEntryOperation.IdFor(name), operationId))
            {
                operation = new RunEntryOperation(name, _store);
                return true;
            }
        }

        return false;
    }
}
