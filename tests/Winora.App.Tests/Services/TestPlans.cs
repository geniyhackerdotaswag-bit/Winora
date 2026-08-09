using Winora.Core.Changes;
using Winora.Core.Contracts;

namespace Winora.App.Tests.Services;

/// <summary>A minimal, valid change plan for tests that need one but do not care what it says.</summary>
internal static class TestPlans
{
    internal static ChangePlan Sample(Guid? planId = null)
    {
        // Canonical algorithm name and uppercase hex: the durable layer rejects anything else, so a
        // lowercase digest here would fail deep inside the journal rather than in the test.
        var source = new StateFingerprint("SHA-256", new string('A', 64));
        var result = new StateFingerprint("SHA-256", new string('B', 64));

        var step = new ChangeStep(
            "step-one",
            new OperationTarget("winora.test.operation"),
            new DisplayValue("winora.value.toggle", "off"),
            new DisplayValue("winora.value.toggle", "on"),
            source,
            result,
            new VerificationProbe("winora.test.read", "on"));

        return ChangePlan.Create(
            planId ?? Guid.NewGuid(),
            "winora.test.operation",
            "winora.category.personalization",
            "Title",
            "Summary",
            [step],
            RiskLevel.Low,
            PrivilegeRequirement.StandardUser,
            RollbackCapability.Full,
            RestartRequirement.None,
            SupportStatus.Supported,
            source,
            new Uri("https://learn.microsoft.com/"),
            BackupRequirement.Required,
            requiresRestorePoint: false);
    }
}
