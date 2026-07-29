using Winora.Core.Changes;
using Winora.System.Safety;
using Xunit;

namespace Winora.System.Tests.Operations;

public sealed class OperationCapabilityTests
{
    private static readonly StateFingerprint Known = new("SHA-256", "0A1B2C3D");

    [Fact]
    public void Healthy_standard_user_target_is_supported()
    {
        var capability = OperationCapabilityPolicy.Evaluate(Healthy());

        Assert.Equal(SupportStatus.Supported, capability.Support);
        Assert.Equal(PrivilegeRequirement.StandardUser, capability.RequiredPrivilege);
        Assert.Null(capability.BlockReason);
        Assert.Equal(Known, capability.CurrentFingerprint);
    }

    [Fact]
    public void Healthy_administrator_target_with_a_split_token_is_supported_with_elevation()
    {
        var capability = OperationCapabilityPolicy.Evaluate(Healthy() with
        {
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            IsElevationSupportedForAccount = true,
        });

        Assert.Equal(SupportStatus.SupportedWithElevation, capability.Support);
        Assert.Equal(PrivilegeRequirement.Administrator, capability.RequiredPrivilege);
        Assert.Null(capability.BlockReason);
    }

    [Fact]
    public void Administrator_target_without_a_split_token_is_blocked_for_the_account()
    {
        var capability = OperationCapabilityPolicy.Evaluate(Healthy() with
        {
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            IsElevationSupportedForAccount = false,
        });

        Assert.Equal(SupportStatus.Unsupported, capability.Support);
        Assert.Equal(CapabilityBlockCodes.ElevationUnsupportedForCurrentAccount, capability.BlockReason);
    }

    [Fact]
    public void Missing_documented_api_is_unsupported()
    {
        var capability = OperationCapabilityPolicy.Evaluate(Healthy() with { IsApiAvailable = false });

        Assert.Equal(SupportStatus.Unsupported, capability.Support);
        Assert.Equal(CapabilityBlockCodes.ApiNotAvailable, capability.BlockReason);
        Assert.False(capability.IsApiAvailable);
    }

    [Fact]
    public void Unreadable_target_state_is_unknown_and_never_supported()
    {
        var capability = OperationCapabilityPolicy.Evaluate(Healthy() with { IsTargetStateKnown = false });

        Assert.Equal(SupportStatus.Unknown, capability.Support);
        Assert.Equal(CapabilityBlockCodes.TargetStateUnknown, capability.BlockReason);
    }

    [Fact]
    public void Remote_target_is_unsupported()
    {
        var capability = OperationCapabilityPolicy.Evaluate(Healthy() with { IsRemoteTarget = true });

        Assert.Equal(SupportStatus.Unsupported, capability.Support);
        Assert.Equal(CapabilityBlockCodes.TargetRemote, capability.BlockReason);
    }

    [Fact]
    public void Protected_target_is_unsupported()
    {
        var capability = OperationCapabilityPolicy.Evaluate(Healthy() with { IsProtectedTarget = true });

        Assert.Equal(SupportStatus.Unsupported, capability.Support);
        Assert.Equal(CapabilityBlockCodes.TargetProtected, capability.BlockReason);
    }

    [Fact]
    public void Read_only_target_is_unsupported()
    {
        var capability = OperationCapabilityPolicy.Evaluate(Healthy() with { IsWritable = false });

        Assert.Equal(SupportStatus.Unsupported, capability.Support);
        Assert.Equal(CapabilityBlockCodes.TargetNotWritable, capability.BlockReason);
        Assert.False(capability.IsWritable);
    }

    [Fact]
    public void Missing_conditional_mutation_degrades_to_unsupported_for_safe_mutation()
    {
        var capability = OperationCapabilityPolicy.Evaluate(
            Healthy() with { IsConditionalMutationAvailable = false });

        Assert.Equal(SupportStatus.UnsupportedForSafeMutation, capability.Support);
        Assert.Equal(CapabilityBlockCodes.ConditionalMutationUnavailable, capability.BlockReason);
        Assert.False(capability.IsConditionalMutationAvailable);
    }

    [Fact]
    public void Missing_independent_verification_degrades_to_unsupported_for_safe_mutation()
    {
        var capability = OperationCapabilityPolicy.Evaluate(
            Healthy() with { IsVerificationAvailable = false });

        Assert.Equal(SupportStatus.UnsupportedForSafeMutation, capability.Support);
        Assert.Equal(CapabilityBlockCodes.VerificationUnavailable, capability.BlockReason);
    }

    [Fact]
    public void Missing_backup_degrades_to_unsupported_for_safe_mutation()
    {
        var capability = OperationCapabilityPolicy.Evaluate(Healthy() with { IsBackupAvailable = false });

        Assert.Equal(SupportStatus.UnsupportedForSafeMutation, capability.Support);
        Assert.Equal(CapabilityBlockCodes.BackupUnavailable, capability.BlockReason);
    }

    [Fact]
    public void Missing_full_rollback_is_partial()
    {
        var capability = OperationCapabilityPolicy.Evaluate(Healthy() with { IsRollbackAvailable = false });

        Assert.Equal(SupportStatus.Partial, capability.Support);
        Assert.Equal(CapabilityBlockCodes.RollbackNotFull, capability.BlockReason);
    }

    [Fact]
    public void An_absent_api_outranks_every_other_degraded_fact()
    {
        var capability = OperationCapabilityPolicy.Evaluate(Healthy() with
        {
            IsApiAvailable = false,
            IsTargetStateKnown = false,
            IsWritable = false,
            IsConditionalMutationAvailable = false,
            IsBackupAvailable = false,
            IsRollbackAvailable = false,
        });

        Assert.Equal(CapabilityBlockCodes.ApiNotAvailable, capability.BlockReason);
    }

    [Theory]
    [MemberData(nameof(DegradedCases))]
    public void Every_degraded_observation_states_a_stable_block_code(string caseName)
    {
        var capability = OperationCapabilityPolicy.Evaluate(Degraded(caseName));

        Assert.NotEqual(SupportStatus.Supported, capability.Support);
        Assert.NotEqual(SupportStatus.SupportedWithElevation, capability.Support);
        Assert.False(string.IsNullOrWhiteSpace(capability.BlockReason));
        Assert.Contains(capability.BlockReason!, CapabilityBlockCodes.All);
    }

    [Theory]
    [MemberData(nameof(DegradedCases))]
    public void Every_degraded_observation_is_rejected_by_the_core_safety_policy(string caseName)
    {
        var capability = OperationCapabilityPolicy.Evaluate(Degraded(caseName));

        var decision = ChangeSafetyPolicy.Evaluate(PlanFor(capability.RequiredPrivilege), capability);

        Assert.False(decision.IsAllowed);
        Assert.False(string.IsNullOrWhiteSpace(decision.BlockReason));
    }

    [Theory]
    [InlineData(PrivilegeRequirement.StandardUser)]
    [InlineData(PrivilegeRequirement.Administrator)]
    public void A_supported_capability_is_accepted_by_the_core_safety_policy(PrivilegeRequirement privilege)
    {
        var capability = OperationCapabilityPolicy.Evaluate(Healthy() with { RequiredPrivilege = privilege });

        var decision = ChangeSafetyPolicy.Evaluate(PlanFor(privilege), capability);

        Assert.True(decision.IsAllowed);
        Assert.Null(decision.BlockReason);
    }

    [Fact]
    public void Block_codes_are_unique_and_namespaced()
    {
        Assert.Equal(CapabilityBlockCodes.All.Count, CapabilityBlockCodes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            CapabilityBlockCodes.All,
            code => Assert.StartsWith("winora.capability.", code, StringComparison.Ordinal));
    }

    public static TheoryData<string> DegradedCases() =>
    [
        "api",
        "state",
        "remote",
        "protected",
        "read-only",
        "conditional",
        "verification",
        "backup",
        "rollback",
        "elevation",
    ];

    private static CapabilityObservation Degraded(string caseName) => caseName switch
    {
        "api" => Healthy() with { IsApiAvailable = false },
        "state" => Healthy() with { IsTargetStateKnown = false },
        "remote" => Healthy() with { IsRemoteTarget = true },
        "protected" => Healthy() with { IsProtectedTarget = true },
        "read-only" => Healthy() with { IsWritable = false },
        "conditional" => Healthy() with { IsConditionalMutationAvailable = false },
        "verification" => Healthy() with { IsVerificationAvailable = false },
        "backup" => Healthy() with { IsBackupAvailable = false },
        "rollback" => Healthy() with { IsRollbackAvailable = false },
        "elevation" => Healthy() with
        {
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            IsElevationSupportedForAccount = false,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
    };

    private static CapabilityObservation Healthy() => new(
        IsApiAvailable: true,
        IsTargetStateKnown: true,
        IsWritable: true,
        IsRemoteTarget: false,
        IsProtectedTarget: false,
        IsBackupAvailable: true,
        IsVerificationAvailable: true,
        IsRollbackAvailable: true,
        IsConditionalMutationAvailable: true,
        RequiredPrivilege: PrivilegeRequirement.StandardUser,
        IsElevationSupportedForAccount: true,
        CurrentFingerprint: Known);

    private static ChangePlan PlanFor(PrivilegeRequirement privilege) => ChangePlan.Create(
        Guid.Parse("2c9d5c2e-9d5f-4a26-9a0f-3f2c8b1d4e77"),
        "winora.test.capability",
        "Test",
        "Capability probe fixture",
        "A fixture plan used to prove capability and safety policy compose.",
        [
            new ChangeStep(
                "step-1",
                new OperationTarget("winora.test.capability"),
                new DisplayValue("text", "current"),
                new DisplayValue("text", "proposed"),
                Known,
                new StateFingerprint("SHA-256", "FFEEDDCC"),
                new VerificationProbe("probe-1", "proposed")),
        ],
        RiskLevel.Low,
        privilege,
        RollbackCapability.Full,
        RestartRequirement.None,
        privilege == PrivilegeRequirement.Administrator
            ? SupportStatus.SupportedWithElevation
            : SupportStatus.Supported,
        Known,
        new Uri("https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow"),
        BackupRequirement.Required,
        requiresRestorePoint: false);
}
