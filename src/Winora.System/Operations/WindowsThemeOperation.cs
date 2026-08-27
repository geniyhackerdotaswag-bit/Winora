using System.Security.Cryptography;
using System.Text;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.System.Safety;
using Winora.System.Windows;

namespace Winora.System.Operations;

/// <summary>
/// Carries Winora's own colour scheme across to Windows: the mode from the scheme's lightness, the
/// accent from its accent colour.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism is a Windows theme file — a copy of the one already in use with a few lines
/// changed, so wallpaper, cursors and sounds carry over untouched. Applying it opens the Windows
/// Settings window, because that is the only way Windows adopts a theme. That cost is stated on the
/// screen before the button is pressed.
/// </para>
/// <para>
/// Undo re-applies the mode and accent recorded before the change, onto whatever theme is current
/// at that moment. Windows rewrites and deletes applied theme files, so there is no earlier file to
/// go back to — and rebuilding from the current one keeps anything the person changed since.
/// </para>
/// </remarks>
public sealed class WindowsThemeOperation : IOperation, IConditionalSystemMutation
{
    public const string Id = "windows.appearance.theme";

    private const string StepId = "windows-theme";

    private static readonly StateFingerprint UnknownFingerprint = new("SHA-256", new string('0', 64));

    private static readonly Uri Documentation =
        new("https://learn.microsoft.com/en-us/windows/win32/controls/themes-overview");

    private readonly IWindowsThemeState _state;
    private readonly WindowsThemeApplier _applier;
    private readonly Func<bool> _isWritable;

    public WindowsThemeOperation(
        IWindowsThemeState state,
        WindowsThemeApplier applier,
        Func<bool>? isWritable = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _isWritable = isWritable ?? WindowsThemeState.IsThemesFolderWritable;
    }

    public string OperationId => Id;

    /// <summary>
    /// Read the live state, compare it with what the plan expected, hand the theme over, then read
    /// it back independently and compare again.
    /// </summary>
    public string ConditionalMutationMechanismId => "windows.theme.checked-apply-confirmed-by-read";

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

        if (!StringComparer.Ordinal.Equals(draft.ProposedValue.Kind, WindowsThemeValues.Kind) ||
            !WindowsThemeValues.TryParse(draft.ProposedValue.Text, out var proposed))
        {
            throw new ArgumentException("The proposed value is not a Windows appearance.", nameof(draft));
        }

        var current = _state.Read();

        if (current.Mode is null)
        {
            throw new InvalidOperationException(
                "A dry run requires the current appearance to be readable.");
        }

        if (Text(current) == Text(proposed))
        {
            // No dead actions: a plan that changes nothing is never offered for confirmation.
            throw new InvalidOperationException("Windows already has this appearance.");
        }

        var source = Fingerprint(current);
        var result = Fingerprint(proposed);

        var step = new ChangeStep(
            StepId,
            new OperationTarget(Id),
            new DisplayValue(WindowsThemeValues.Kind, Text(current)),
            new DisplayValue(WindowsThemeValues.Kind, Text(proposed)),
            source,
            result,
            new VerificationProbe($"{Id}.read", Text(proposed)));

        return ValueTask.FromResult(ChangePlan.Create(
            Guid.NewGuid(),
            Id,
            draft.Category,
            draft.Title,
            draft.Summary,
            [step],
            // The whole desktop changes colour. Nothing here is hard to undo, but calling a
            // system-wide appearance change low risk would understate what the person will see.
            RiskLevel.Medium,
            PrivilegeRequirement.StandardUser,
            RollbackCapability.Full,
            RestartRequirement.None,
            OperationCapabilityPolicy.Evaluate(Observe()).Support,
            source,
            Documentation,
            BackupRequirement.Required,
            requiresRestorePoint: false));
    }

    public async ValueTask<StepResult> ApplyStepAsync(
        ChangePlan plan,
        ChangeStep step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureStep(step);

        return !WindowsThemeValues.TryParse(step.ProposedValue.Text, out var proposed)
            ? StepResult.NotApplied("The step does not carry a readable appearance.")
            : await MutateAsync(step.SourceFingerprint, step.ResultFingerprint, proposed, cancellationToken)
                .ConfigureAwait(false);
    }

    public ValueTask<VerificationResult> VerifyStepAsync(
        ChangePlan plan,
        ChangeStep step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureStep(step);
        cancellationToken.ThrowIfCancellationRequested();

        // An independent read of the registry, not an echo of what was handed to Windows.
        var live = _state.Read();

        if (live.Mode is null)
        {
            return ValueTask.FromResult(
                VerificationResult.Failed(UnknownFingerprint, "The applied appearance could not be read back."));
        }

        var observed = Fingerprint(live);

        return ValueTask.FromResult(observed == step.ResultFingerprint
            ? VerificationResult.Passed(observed)
            : VerificationResult.Failed(observed, "The observed appearance does not match the confirmed result."));
    }

    public async ValueTask<StepResult> RollbackStepAsync(
        RollbackPlan plan,
        ChangeStep step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureStep(step);

        if (!WindowsThemeValues.TryParse(step.CurrentValue.Text, out var source))
        {
            return StepResult.NotApplied("The step does not carry a readable source appearance.");
        }

        var live = _state.Read();

        if (live.Mode is null)
        {
            return StepResult.FailedOutcomeUnknown("The current appearance could not be read before rollback.");
        }

        var observed = Fingerprint(live);

        // Idempotence: a repeated rollback observes the source state and applies nothing.
        if (observed == step.SourceFingerprint)
        {
            return StepResult.AlreadyRestored(observed);
        }

        if (observed != step.ResultFingerprint)
        {
            return StepResult.NotApplied(
                "The appearance is neither the applied nor the source one; rollback needs conflict review.");
        }

        return await MutateAsync(step.ResultFingerprint, step.SourceFingerprint, source, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The sequence shared by apply and rollback: re-check the expected state, hand the theme over,
    /// then read back and compare.
    /// </summary>
    /// <remarks>
    /// Every outcome the applier can report is turned into a distinct result. A theme that was
    /// handed over and never adopted reports the state as unchanged rather than as applied — that
    /// case is real, it happens whenever a Settings window is already open, and it produces no error
    /// of its own to notice.
    /// </remarks>
    private async ValueTask<StepResult> MutateAsync(
        StateFingerprint expected,
        StateFingerprint intended,
        WindowsThemeSettings wanted,
        CancellationToken cancellationToken)
    {
        var before = _state.Read();

        if (before.Mode is null)
        {
            return StepResult.NotApplied("The current appearance could not be read before the change.");
        }

        if (Fingerprint(before) != expected)
        {
            return StepResult.NotApplied("The appearance changed after the dry run and was not overwritten.");
        }

        var outcome = await _applier.ApplyAsync(wanted, cancellationToken).ConfigureAwait(false);

        if (outcome != WindowsThemeApplyOutcome.Applied)
        {
            var after = _state.Read();

            // The theme file is written before it is handed over, and nothing else is touched. If
            // the appearance still reads as it did, nothing changed and that can be said plainly.
            return after.Mode is not null && Fingerprint(after) == expected
                ? StepResult.NotApplied(Detail(outcome))
                : StepResult.FailedOutcomeUnknown(Detail(outcome));
        }

        var applied = _state.Read();

        if (applied.Mode is null)
        {
            return StepResult.FailedOutcomeUnknown("The appearance could not be read back after the change.");
        }

        var observed = Fingerprint(applied);

        return observed == intended
            ? StepResult.Applied(observed)
            : StepResult.FailedOutcomeUnknown("Windows reported the theme applied but the appearance does not match.");
    }

    private static string Detail(WindowsThemeApplyOutcome outcome) => outcome switch
    {
        WindowsThemeApplyOutcome.CurrentThemeMissing => "The theme Windows says is current is not on disk.",
        WindowsThemeApplyOutcome.CouldNotWrite => "The theme file could not be written.",
        WindowsThemeApplyOutcome.SettingsWindowOpen =>
            "A Windows Settings window is open, and it takes the theme without applying it.",
        _ => "Windows was given the theme and the appearance never changed.",
    };

    private CapabilityObservation Observe()
    {
        var path = _state.CurrentThemePath();
        var hasTheme = path is not null && File.Exists(path);
        var live = _state.Read();
        var isKnown = hasTheme && live.Mode is not null;
        var isWritable = isKnown && _isWritable();

        return new CapabilityObservation(
            IsApiAvailable: hasTheme,
            IsTargetStateKnown: isKnown,
            IsWritable: isWritable,
            IsRemoteTarget: false,
            IsProtectedTarget: false,

            // The source state is the recorded mode and accent, which is exactly what rollback
            // re-applies. There is no separate file to keep: Windows rewrites and deletes the ones
            // it adopts.
            IsBackupAvailable: isKnown,
            IsVerificationAvailable: isKnown,
            IsRollbackAvailable: isKnown,
            IsConditionalMutationAvailable: isKnown,
            RequiredPrivilege: PrivilegeRequirement.StandardUser,
            IsElevationSupportedForAccount: true,
            CurrentFingerprint: isKnown ? Fingerprint(live) : UnknownFingerprint,
            CurrentValue: isKnown ? new DisplayValue(WindowsThemeValues.Kind, Text(live)) : null,
            ApiUnavailableCode: CapabilityBlockCodes.CurrentThemeMissing);
    }

    private static string Text(WindowsThemeSettings settings) => WindowsThemeValues.For(settings);

    private static StateFingerprint Fingerprint(WindowsThemeSettings settings) => new(
        "SHA-256",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Id}={Text(settings)}"))));

    private static void EnsureTarget(OperationTarget target, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(target, parameterName);

        if (!StringComparer.Ordinal.Equals(target.TargetId, Id))
        {
            throw new ArgumentException("The target belongs to another operation.", parameterName);
        }
    }

    private static void EnsureStep(ChangeStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        EnsureTarget(step.Target, nameof(step));

        if (!StringComparer.Ordinal.Equals(step.StepId, StepId))
        {
            throw new ArgumentException("The step belongs to another operation.", nameof(step));
        }
    }
}
