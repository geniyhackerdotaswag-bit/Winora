using Winora.Core.Changes;

namespace Winora.System.Safety;

/// <summary>
/// The raw, read-only facts a Windows adapter observes about one target. An observation states
/// what was seen; it never decides whether a mutation is allowed. That decision belongs to
/// <see cref="OperationCapabilityPolicy"/> so that every adapter degrades identically.
/// </summary>
/// <param name="IsApiAvailable">
/// The documented Windows API, registry integration point, or shell action exists on this build.
/// </param>
/// <param name="IsTargetStateKnown">
/// The current state was read completely and <paramref name="CurrentFingerprint"/> describes it.
/// </param>
/// <param name="IsWritable">The current user can write the target without elevation beyond
/// <paramref name="RequiredPrivilege"/>.</param>
/// <param name="IsRemoteTarget">The target resolves to a network or otherwise remote location.</param>
/// <param name="IsProtectedTarget">The target is an operating-system protected location.</param>
/// <param name="IsBackupAvailable">An exact, verifiable backup of the source state can be produced.</param>
/// <param name="IsVerificationAvailable">
/// The result can be verified by a probe independent of the write that produced it.
/// </param>
/// <param name="IsRollbackAvailable">Rollback can restore the exact source state and is idempotent.</param>
/// <param name="IsConditionalMutationAvailable">
/// A documented conditional mechanism protects the expected-state comparison through the write.
/// </param>
/// <param name="RequiredPrivilege">The privilege the confirmed plan would need.</param>
/// <param name="IsElevationSupportedForAccount">
/// The interactive account has an administrator split token, so consent elevation can succeed.
/// Irrelevant when <paramref name="RequiredPrivilege"/> is
/// <see cref="PrivilegeRequirement.StandardUser"/>.
/// </param>
/// <param name="CurrentFingerprint">The fingerprint of the observed source state.</param>
/// <param name="CurrentValue">
/// The observed source state in the adapter's stable value vocabulary, or null when the state could
/// not be read. A fingerprint is a hash and cannot be turned back into a value, so a presentation
/// layer that must show the current state needs this alongside it. It is the same read, so the two
/// can never disagree.
/// </param>
public sealed record CapabilityObservation(
    bool IsApiAvailable,
    bool IsTargetStateKnown,
    bool IsWritable,
    bool IsRemoteTarget,
    bool IsProtectedTarget,
    bool IsBackupAvailable,
    bool IsVerificationAvailable,
    bool IsRollbackAvailable,
    bool IsConditionalMutationAvailable,
    PrivilegeRequirement RequiredPrivilege,
    bool IsElevationSupportedForAccount,
    StateFingerprint CurrentFingerprint,
    DisplayValue? CurrentValue = null,

    /// <param name="NotWritableCode">
    /// The block code to report when <paramref name="IsWritable"/> is false, when the operation
    /// knows something more useful than "not writable". Null takes the general answer.
    /// </param>
    /// <remarks>
    /// Exists because the general answer was actively misleading in the one place it came up most.
    /// See <c>CapabilityBlockCodes.DependentSwitchOff</c>.
    /// </remarks>
    string? NotWritableCode = null,

    /// <param name="ApiUnavailableCode">
    /// The block code to report when <paramref name="IsApiAvailable"/> is false, when the operation
    /// knows something more useful than "not available on this build". Null takes the general answer.
    /// </param>
    /// <remarks>
    /// The same reasoning as <paramref name="NotWritableCode"/>, and it earned its place the same
    /// way: by producing a message that was true of nothing and led nowhere.
    /// </remarks>
    string? ApiUnavailableCode = null);
