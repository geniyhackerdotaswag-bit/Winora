using System.Text;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.System.Operations;
using Winora.System.Safety;
using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Operations;

/// <summary>
/// The Windows appearance as a change with a plan, a source state, a check and an undo.
/// </summary>
/// <remarks>
/// The interesting cases are the ones where Windows takes the theme and does nothing with it. That
/// is not a hypothetical: it happens whenever a Settings window is already open, it produces no
/// error, and an operation that reported success on the handover would record a change to the
/// journal that never took place — and then offer to undo it.
/// </remarks>
public sealed class WindowsThemeOperationTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "winora-theme-op-" + Guid.NewGuid().ToString("N"));

    private const string Sample =
        "[Theme]\r\n" +
        "DisplayName=Sample\r\n" +
        "\r\n" +
        "[VisualStyles]\r\n" +
        "AutoColorization=1\r\n" +
        "ColorizationColor=0XC4533222\r\n" +
        "SystemMode=Dark\r\n" +
        "AppMode=Dark\r\n";

    public WindowsThemeOperationTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temporary folder is not worth failing a test over.
        }
    }

    /// <summary>Windows, with the change arriving only if something tells it to.</summary>
    private sealed class FakeState : IWindowsThemeState
    {
        public WindowsThemeSettings Settings { get; set; } = new(WindowsThemeMode.Dark, 0x533222);

        public string? Path { get; set; }

        public WindowsThemeSettings Read() => Settings;

        public string? CurrentThemePath() => Path;
    }

    /// <summary>A launcher that moves the fake system, or deliberately does not.</summary>
    private sealed class FakeLauncher : IThemeLauncher
    {
        private readonly FakeState _state;

        public FakeLauncher(FakeState state) => _state = state;

        public bool SettingsOpen { get; set; }

        /// <summary>What Windows ends up holding, or null to have it ignore the theme.</summary>
        public WindowsThemeSettings? Adopts { get; set; }

        public void Start(string themePath)
        {
            if (Adopts is { } adopted)
            {
                _state.Settings = adopted;
            }
        }

        public bool IsSettingsOpen() => SettingsOpen;
    }

    private string WriteSample()
    {
        var path = Path.Combine(_folder, "Current.theme");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(Sample));
        return path;
    }

    private (WindowsThemeOperation Operation, FakeState State, FakeLauncher Launcher) Build(bool writable = true)
    {
        var state = new FakeState { Path = WriteSample() };
        var launcher = new FakeLauncher(state);
        var applier = new WindowsThemeApplier(
            state,
            launcher,
            Path.Combine(_folder, "themes"),
            attempts: 3,
            pause: TimeSpan.Zero);

        return (new WindowsThemeOperation(state, applier, () => writable), state, launcher);
    }

    private static OperationDraft Draft(string proposed) => new(
        WindowsThemeOperation.Id,
        "appearance",
        "Windows appearance",
        "Carry the scheme across to Windows.",
        new OperationTarget(WindowsThemeOperation.Id),
        new DisplayValue(WindowsThemeValues.Kind, proposed));

    private static ChangeStep Step(ChangePlan plan) => plan.Steps[0];

    private static RollbackPlan RollbackFor(ChangePlan plan) => RollbackPlan.Create(
        Guid.NewGuid(),
        plan,
        BackupReceipt.Verified("backup", "BACKUP-DIGEST", plan.Digest, plan.SourceFingerprint, plan.SourceFingerprint),
        plan.Steps[^1].ResultFingerprint);

    [Fact]
    public async Task A_readable_appearance_is_reported_as_changeable()
    {
        var (operation, _, _) = Build();

        var capability = await operation.ProbeAsync(new OperationTarget(WindowsThemeOperation.Id), default);

        Assert.Equal(SupportStatus.Supported, capability.Support);
        Assert.Equal(PrivilegeRequirement.StandardUser, capability.RequiredPrivilege);
        Assert.Equal("dark 533222", capability.CurrentValue?.Text);
    }

    /// <summary>Winora never asks for rights a per-user setting does not need.</summary>
    [Fact]
    public async Task A_folder_that_cannot_be_written_blocks_the_change_without_asking_for_rights()
    {
        var (operation, _, _) = Build(writable: false);

        var capability = await operation.ProbeAsync(new OperationTarget(WindowsThemeOperation.Id), default);

        Assert.Equal(SupportStatus.Unsupported, capability.Support);
        Assert.Equal(PrivilegeRequirement.StandardUser, capability.RequiredPrivilege);
    }

    [Fact]
    public async Task A_plan_records_both_the_appearance_now_and_the_one_asked_for()
    {
        var (operation, _, _) = Build();

        var plan = await operation.PreviewAsync(Draft("light 1f2356"), default);

        Assert.Equal("dark 533222", Step(plan).CurrentValue.Text);
        Assert.Equal("light 1f2356", Step(plan).ProposedValue.Text);
        Assert.Equal(RollbackCapability.Full, plan.Rollback);
        Assert.Equal(BackupRequirement.Required, plan.Backup);
        Assert.Equal(RestartRequirement.None, plan.Restart);
    }

    /// <summary>A change that changes nothing is never offered.</summary>
    [Fact]
    public async Task The_appearance_Windows_already_has_is_not_offered_as_a_change()
    {
        var (operation, _, _) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await operation.PreviewAsync(Draft("dark 533222"), default));
    }

    [Fact]
    public async Task A_proposal_that_is_not_an_appearance_is_refused()
    {
        var (operation, _, _) = Build();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await operation.PreviewAsync(Draft("chartreuse"), default));
    }

    [Fact]
    public async Task An_applied_change_is_verified_by_reading_the_system_again()
    {
        var (operation, _, launcher) = Build();
        var plan = await operation.PreviewAsync(Draft("light 1f2356"), default);
        launcher.Adopts = new WindowsThemeSettings(WindowsThemeMode.Light, 0x1F2356);

        var applied = await operation.ApplyStepAsync(plan, Step(plan), default);
        var verified = await operation.VerifyStepAsync(plan, Step(plan), default);

        Assert.Equal(StepResultKind.Applied, applied.Kind);
        Assert.True(verified.IsVerified);
    }

    /// <summary>
    /// A theme Windows quietly ignored is reported as no change, not as a change.
    /// </summary>
    /// <remarks>
    /// This is the failure the live experiment produced twice. Nothing throws, nothing logs, and the
    /// only evidence is that the registry still says what it said before.
    /// </remarks>
    [Fact]
    public async Task A_theme_Windows_ignored_is_reported_as_nothing_having_changed()
    {
        var (operation, _, launcher) = Build();
        var plan = await operation.PreviewAsync(Draft("light 1f2356"), default);
        launcher.Adopts = null;

        var result = await operation.ApplyStepAsync(plan, Step(plan), default);

        Assert.Equal(StepResultKind.NotApplied, result.Kind);
    }

    [Fact]
    public async Task An_open_settings_window_stops_the_change_and_says_which_window()
    {
        var (operation, _, launcher) = Build();
        var plan = await operation.PreviewAsync(Draft("light 1f2356"), default);
        launcher.SettingsOpen = true;

        var result = await operation.ApplyStepAsync(plan, Step(plan), default);

        Assert.Equal(StepResultKind.NotApplied, result.Kind);
        Assert.Contains("Settings", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>An appearance that moved since the dry run is not overwritten.</summary>
    [Fact]
    public async Task An_appearance_that_changed_since_the_dry_run_is_left_alone()
    {
        var (operation, state, launcher) = Build();
        var plan = await operation.PreviewAsync(Draft("light 1f2356"), default);

        state.Settings = new WindowsThemeSettings(WindowsThemeMode.Dark, 0x112233);
        launcher.Adopts = new WindowsThemeSettings(WindowsThemeMode.Light, 0x1F2356);

        var result = await operation.ApplyStepAsync(plan, Step(plan), default);

        Assert.Equal(StepResultKind.NotApplied, result.Kind);
    }

    [Fact]
    public async Task Undo_puts_back_the_appearance_that_was_recorded()
    {
        var (operation, _, launcher) = Build();
        var plan = await operation.PreviewAsync(Draft("light 1f2356"), default);

        launcher.Adopts = new WindowsThemeSettings(WindowsThemeMode.Light, 0x1F2356);
        await operation.ApplyStepAsync(plan, Step(plan), default);

        launcher.Adopts = new WindowsThemeSettings(WindowsThemeMode.Dark, 0x533222);
        var rollback = RollbackFor(plan);

        var result = await operation.RollbackStepAsync(rollback, Step(plan), default);

        Assert.Equal(StepResultKind.Applied, result.Kind);
    }

    /// <summary>Undoing twice is not an error and is not a second change.</summary>
    [Fact]
    public async Task Undoing_an_appearance_that_is_already_back_changes_nothing()
    {
        var (operation, _, launcher) = Build();
        var plan = await operation.PreviewAsync(Draft("light 1f2356"), default);

        launcher.Adopts = new WindowsThemeSettings(WindowsThemeMode.Light, 0x1F2356);
        await operation.ApplyStepAsync(plan, Step(plan), default);

        launcher.Adopts = new WindowsThemeSettings(WindowsThemeMode.Dark, 0x533222);
        var rollback = RollbackFor(plan);
        await operation.RollbackStepAsync(rollback, Step(plan), default);

        var again = await operation.RollbackStepAsync(rollback, Step(plan), default);

        Assert.Equal(StepResultKind.AlreadyRestored, again.Kind);
    }

    /// <summary>
    /// An appearance that is neither the applied nor the original one is not overwritten.
    /// </summary>
    /// <remarks>
    /// Somebody changed it themselves in between. Putting back the old value here would undo their
    /// change, not Winora's.
    /// </remarks>
    [Fact]
    public async Task Undo_refuses_when_somebody_else_changed_the_appearance()
    {
        var (operation, state, launcher) = Build();
        var plan = await operation.PreviewAsync(Draft("light 1f2356"), default);

        launcher.Adopts = new WindowsThemeSettings(WindowsThemeMode.Light, 0x1F2356);
        await operation.ApplyStepAsync(plan, Step(plan), default);

        state.Settings = new WindowsThemeSettings(WindowsThemeMode.Light, 0x998877);
        var rollback = RollbackFor(plan);

        var result = await operation.RollbackStepAsync(rollback, Step(plan), default);

        Assert.Equal(StepResultKind.NotApplied, result.Kind);
    }

    [Fact]
    public async Task A_step_belonging_to_another_operation_is_refused()
    {
        var (operation, _, _) = Build();
        var plan = await operation.PreviewAsync(Draft("light 1f2356"), default);

        var foreign = Step(plan) with { Target = new OperationTarget("windows.something.else") };

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await operation.ApplyStepAsync(plan, foreign, default));
    }

    /// <summary>
    /// A current theme Windows names but does not have gets its own reason.
    /// </summary>
    /// <remarks>
    /// Reached during testing on a real machine. The general answer — "this build of Windows does
    /// not support the setting" — is untrue and leads nowhere; picking any theme in Settings fixes
    /// it, and Windows deletes theme files itself, so nobody has to do anything odd to land here.
    /// </remarks>
    [Fact]
    public async Task A_current_theme_that_is_not_on_disk_says_so_rather_than_blaming_Windows()
    {
        var (operation, state, _) = Build();
        state.Path = Path.Combine(_folder, "deleted.theme");

        var capability = await operation.ProbeAsync(new OperationTarget(WindowsThemeOperation.Id), default);

        Assert.Equal(SupportStatus.Unsupported, capability.Support);
        Assert.Equal(CapabilityBlockCodes.CurrentThemeMissing, capability.BlockReason);
    }
}
