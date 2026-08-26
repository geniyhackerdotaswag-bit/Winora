namespace Winora.System.Safety;

/// <summary>
/// Stable, localizable reason codes for a blocked capability. Adapters never emit user-facing
/// prose: <c>Winora.App</c> resolves these codes through its <c>.resw</c> resources, and the
/// action journal stores the code rather than a path, value, or exception payload.
/// </summary>
public static class CapabilityBlockCodes
{
    private const string Prefix = "winora.capability.";

    /// <summary>The documented Windows API or action is not present on this build.</summary>
    public const string ApiNotAvailable = Prefix + "api-not-available";

    /// <summary>The current target state could not be read, so no fingerprint can be trusted.</summary>
    public const string TargetStateUnknown = Prefix + "target-state-unknown";

    /// <summary>The target resolves to a remote or network location.</summary>
    public const string TargetRemote = Prefix + "target-remote";

    /// <summary>The target is an operating-system protected location Winora never mutates.</summary>
    public const string TargetProtected = Prefix + "target-protected";

    /// <summary>The target exists and is readable but cannot be written by this user.</summary>
    public const string TargetNotWritable = Prefix + "target-not-writable";

    /// <summary>
    /// The setting depends on another one that is switched off.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="TargetNotWritable"/> on 2026-08-26. Eleven visual effects report
    /// this, and every one of them was reading "Настройку видно, но записать её этой учётной
    /// записи нельзя" — which sent the owner, and then me, looking for administrator rights that
    /// these settings have never needed. They are per-user and want no privilege at all; what
    /// blocks them is the "Эффекты интерфейса" master switch being off, at which point Windows
    /// ignores whatever they are set to.
    /// </remarks>
    public const string DependentSwitchOff = Prefix + "dependent-switch-off";

    /// <summary>
    /// The plan needs administrator rights, but the interactive account has no administrator
    /// split token. Winora reports this before review and never raises UAC for it.
    /// </summary>
    public const string ElevationUnsupportedForCurrentAccount =
        Prefix + "elevation-unsupported-for-current-account";

    /// <summary>
    /// No documented conditional mechanism can protect the expected-state check through the
    /// write, so the operation degrades instead of using an unconditional fallback.
    /// </summary>
    public const string ConditionalMutationUnavailable = Prefix + "conditional-mutation-unavailable";

    /// <summary>The result cannot be verified independently of the write that produced it.</summary>
    public const string VerificationUnavailable = Prefix + "verification-unavailable";

    /// <summary>An exact, verifiable backup of the source state cannot be produced.</summary>
    public const string BackupUnavailable = Prefix + "backup-unavailable";

    /// <summary>Rollback would be partial, so direct mutation is not offered.</summary>
    public const string RollbackNotFull = Prefix + "rollback-not-full";

    public static IReadOnlyList<string> All { get; } =
    [
        ApiNotAvailable,
        TargetStateUnknown,
        TargetRemote,
        TargetProtected,
        TargetNotWritable,
        DependentSwitchOff,
        ElevationUnsupportedForCurrentAccount,
        ConditionalMutationUnavailable,
        VerificationUnavailable,
        BackupUnavailable,
        RollbackNotFull,
    ];
}
