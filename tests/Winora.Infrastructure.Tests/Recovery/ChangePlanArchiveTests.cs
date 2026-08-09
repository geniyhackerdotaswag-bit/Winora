using Winora.Core.Changes;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Recovery;
using Xunit;

namespace Winora.Infrastructure.Tests.Recovery;

/// <summary>
/// The archive exists so a half-finished operation can still be rolled back. Its one hard promise
/// is that the plan comes back <em>identical</em>: the coordinator compares the plan's digest with
/// the one recorded in the durable boundary, so a round trip that changes any field would be
/// rejected as external drift and leave the operation stuck exactly as before.
/// </summary>
public sealed class ChangePlanArchiveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("winora-plan-archive").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    [Fact]
    public async Task A_saved_plan_comes_back_with_the_same_digest()
    {
        var archive = new ChangePlanArchive(new WinoraDataPaths(_root));
        var plan = CreatePlan();

        await archive.SaveAsync(plan, CancellationToken.None);
        var loaded = await archive.TryLoadAsync(plan.PlanId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(plan.Digest, loaded!.Digest);
        Assert.Equal(plan.PlanId, loaded.PlanId);
        Assert.Equal(plan.SourceFingerprint, loaded.SourceFingerprint);
        Assert.Equal(
            plan.Steps.Select(static step => step.StepId),
            loaded.Steps.Select(static step => step.StepId));
    }

    [Fact]
    public async Task Every_step_field_survives_the_round_trip()
    {
        var archive = new ChangePlanArchive(new WinoraDataPaths(_root));
        var plan = CreatePlan();

        await archive.SaveAsync(plan, CancellationToken.None);
        var loaded = await archive.TryLoadAsync(plan.PlanId, CancellationToken.None);

        var original = plan.Steps[0];
        var restored = Assert.Single(loaded!.Steps);
        Assert.Equal(original, restored);
    }

    /// <summary>
    /// Operations planned before the archive existed have no file. That is an ordinary answer, and
    /// recovery must be able to say "I cannot" rather than throw.
    /// </summary>
    [Fact]
    public async Task An_absent_plan_is_reported_as_absent()
    {
        var archive = new ChangePlanArchive(new WinoraDataPaths(_root));

        var loaded = await archive.TryLoadAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(loaded);
    }

    /// <summary>A truncated or corrupt archive must not be guessed at either.</summary>
    [Fact]
    public async Task A_corrupt_plan_is_reported_as_absent_rather_than_throwing()
    {
        var paths = new WinoraDataPaths(_root);
        var archive = new ChangePlanArchive(paths);
        var plan = CreatePlan();
        await archive.SaveAsync(plan, CancellationToken.None);

        var file = Path.Combine(paths.OperationsDirectory, plan.PlanId.ToString("N"), "plan.json");
        await File.WriteAllTextAsync(file, "{ not json", CancellationToken.None);

        var loaded = await archive.TryLoadAsync(plan.PlanId, CancellationToken.None);

        Assert.Null(loaded);
    }

    private static ChangePlan CreatePlan()
    {
        var source = new StateFingerprint("SHA-256", new string('a', 64));
        var result = new StateFingerprint("SHA-256", new string('b', 64));
        var step = new ChangeStep(
            "visual-effects-menu-animation",
            new OperationTarget("winora.visual-effects.menu-animation"),
            new DisplayValue("winora.value.toggle", "off"),
            new DisplayValue("winora.value.toggle", "on"),
            source,
            result,
            new VerificationProbe("winora.visual-effects.menu-animation.read", "on"));

        return ChangePlan.Create(
            Guid.NewGuid(),
            "winora.visual-effects.menu-animation",
            "winora.category.personalization",
            "Анимация меню",
            "Включает анимацию меню.",
            [step],
            RiskLevel.Low,
            PrivilegeRequirement.StandardUser,
            RollbackCapability.Full,
            RestartRequirement.None,
            SupportStatus.Supported,
            source,
            new Uri("https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-systemparametersinfow"),
            BackupRequirement.Required,
            requiresRestorePoint: false);
    }
}
