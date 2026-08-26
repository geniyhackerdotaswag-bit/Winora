using System.Text;
using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Windows;

/// <summary>
/// Applying a Windows theme, and not calling it done until Windows agrees.
/// </summary>
/// <remarks>
/// <para>
/// Windows adopts a theme through its own Settings process, so handing over the file returns before
/// anything changes. A version of this that reported success on the handover would be right most of
/// the time and lying the rest — and "most of the time" is exactly how the wrong answer survives a
/// manual check.
/// </para>
/// <para>
/// Most of the cases below exist because a live experiment on 2026-08-27 found the behaviour they
/// describe. The first version of this class passed a full suite of tests written from the format's
/// documentation and would not have worked on the machine it was written on.
/// </para>
/// </remarks>
public sealed class WindowsThemeApplierTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "winora-theme-" + Guid.NewGuid().ToString("N"));

    /// <summary>The shape Windows actually writes, eight-digit colour and auto-accent included.</summary>
    private const string Sample =
        "[Theme]\r\n" +
        "DisplayName=Sample\r\n" +
        "\r\n" +
        "[Control Panel\\Desktop]\r\n" +
        "Wallpaper=%USERPROFILE%\\picture.jpg\r\n" +
        "\r\n" +
        "[VisualStyles]\r\n" +
        "AutoColorization=1\r\n" +
        "ColorizationColor=0XC4533222\r\n" +
        "SystemMode=Dark\r\n" +
        "AppMode=Dark\r\n";

    public WindowsThemeApplierTests() => Directory.CreateDirectory(_folder);

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

    /// <summary>Windows as seen from here: what it holds, and what file it says is current.</summary>
    private sealed class FakeState : IWindowsThemeState
    {
        public WindowsThemeSettings Settings { get; set; } = new(WindowsThemeMode.Dark, 0x533222);

        public string? Path { get; set; }

        /// <summary>Reads before the change shows up, so a caller that does not wait fails.</summary>
        public int ChangesAfterReads { get; set; } = int.MaxValue;

        public WindowsThemeSettings Becomes { get; set; } = new(WindowsThemeMode.Light, 0x10FF10);

        public int Reads { get; private set; }

        public WindowsThemeSettings Read()
        {
            Reads++;
            return Reads > ChangesAfterReads ? Becomes : Settings;
        }

        public string? CurrentThemePath() => Path;
    }

    private sealed class FakeLauncher : IThemeLauncher
    {
        public string? Started { get; private set; }

        public bool SettingsOpen { get; set; }

        public void Start(string themePath) => Started = themePath;

        public bool IsSettingsOpen() => SettingsOpen;
    }

    private string WriteSample(string name = "Current.theme")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(Sample));
        return path;
    }

    private string ThemesFolder => Path.Combine(_folder, "themes");

    private WindowsThemeApplier Build(FakeState state, FakeLauncher launcher) =>
        new(state, launcher, ThemesFolder, attempts: 5, pause: TimeSpan.Zero);

    private static string Read(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));

    [Fact]
    public async Task With_no_current_theme_on_disk_nothing_is_attempted()
    {
        var launcher = new FakeLauncher();
        var applier = Build(new FakeState { Path = Path.Combine(_folder, "gone.theme") }, launcher);

        var outcome = await applier.ApplyAsync(new WindowsThemeSettings(WindowsThemeMode.Light, Accent: null));

        Assert.Equal(WindowsThemeApplyOutcome.CurrentThemeMissing, outcome);
        Assert.Null(launcher.Started);
    }

    [Fact]
    public async Task The_edited_copy_is_what_gets_handed_over()
    {
        var state = new FakeState { Path = WriteSample(), ChangesAfterReads = 0 };
        var launcher = new FakeLauncher();

        var outcome = await Build(state, launcher).ApplyAsync(new WindowsThemeSettings(WindowsThemeMode.Light, 0x10FF10));

        Assert.Equal(WindowsThemeApplyOutcome.Applied, outcome);
        Assert.NotNull(launcher.Started);

        var written = Read(launcher.Started);
        Assert.Contains("SystemMode=Light", written, StringComparison.Ordinal);
        Assert.Contains("AppMode=Light", written, StringComparison.Ordinal);

        // The alpha byte the file already carried, kept; the auto-accent flag, cleared.
        Assert.Contains("ColorizationColor=0XC410FF10", written, StringComparison.Ordinal);
        Assert.Contains("AutoColorization=0", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wallpaper line has to survive: this changes colours, and a person who changes colours
    /// has not asked for a different desktop picture.
    /// </summary>
    [Fact]
    public async Task Everything_the_theme_said_about_the_desktop_carries_over()
    {
        var state = new FakeState { Path = WriteSample(), ChangesAfterReads = 0 };
        var launcher = new FakeLauncher();

        await Build(state, launcher).ApplyAsync(new WindowsThemeSettings(WindowsThemeMode.Light, Accent: null));

        Assert.Contains("Wallpaper=%USERPROFILE%\\picture.jpg", Read(launcher.Started!), StringComparison.Ordinal);
    }

    /// <summary>Handing the file over is not the same as the theme changing.</summary>
    [Fact]
    public async Task A_change_that_never_arrives_is_a_failure_not_a_success()
    {
        var state = new FakeState { Path = WriteSample() };
        var launcher = new FakeLauncher();

        var outcome = await Build(state, launcher).ApplyAsync(new WindowsThemeSettings(WindowsThemeMode.Light, Accent: null));

        Assert.Equal(WindowsThemeApplyOutcome.NotConfirmed, outcome);
        Assert.NotNull(launcher.Started);
    }

    /// <summary>Confirmation waits rather than reading once and giving up.</summary>
    [Fact]
    public async Task A_change_that_takes_a_moment_is_still_confirmed()
    {
        var state = new FakeState { Path = WriteSample(), ChangesAfterReads = 3 };
        var launcher = new FakeLauncher();

        var outcome = await Build(state, launcher).ApplyAsync(new WindowsThemeSettings(WindowsThemeMode.Light, Accent: null));

        Assert.Equal(WindowsThemeApplyOutcome.Applied, outcome);
        Assert.True(state.Reads > 1);
    }

    /// <summary>
    /// The mode arriving is not enough when a colour was asked for too.
    /// </summary>
    /// <remarks>
    /// This is the shape of the failure the live experiment produced: the theme was adopted, the
    /// mode was right, and the colour was quietly ignored. Confirming on the mode alone would have
    /// called that a success.
    /// </remarks>
    [Fact]
    public async Task A_colour_that_does_not_arrive_fails_even_when_the_mode_does()
    {
        var state = new FakeState
        {
            Path = WriteSample(),
            ChangesAfterReads = 0,
            Becomes = new WindowsThemeSettings(WindowsThemeMode.Light, 0x533222),
        };

        var outcome = await Build(state, new FakeLauncher()).ApplyAsync(new WindowsThemeSettings(WindowsThemeMode.Light, 0x10FF10));

        Assert.Equal(WindowsThemeApplyOutcome.NotConfirmed, outcome);
    }

    /// <summary>
    /// A second press must not land on the file Windows is already using.
    /// </summary>
    /// <remarks>
    /// Windows does nothing with a theme that is already current. With one fixed name the first
    /// press works and every later one silently does not — and a developer who presses once never
    /// sees it.
    /// </remarks>
    [Fact]
    public async Task A_second_change_is_written_somewhere_Windows_is_not_already_using()
    {
        var first = new FakeState { Path = WriteSample(), ChangesAfterReads = 0 };
        var launcher = new FakeLauncher();
        var applier = Build(first, launcher);

        await applier.ApplyAsync(new WindowsThemeSettings(WindowsThemeMode.Light, 0x10FF10));
        var firstFile = launcher.Started!;

        // Windows now reports the file it was just given as the current theme.
        var second = new FakeState { Path = firstFile, ChangesAfterReads = 0 };
        var secondLauncher = new FakeLauncher();
        await Build(second, secondLauncher).ApplyAsync(new WindowsThemeSettings(WindowsThemeMode.Dark, 0x112233));

        Assert.NotEqual(firstFile, secondLauncher.Started);
    }

    /// <summary>An open Settings window swallows the request, and that is said rather than hidden.</summary>
    [Fact]
    public async Task An_open_settings_window_is_reported_instead_of_being_worked_around()
    {
        var state = new FakeState { Path = WriteSample(), ChangesAfterReads = 0 };
        var launcher = new FakeLauncher { SettingsOpen = true };

        var outcome = await Build(state, launcher).ApplyAsync(new WindowsThemeSettings(WindowsThemeMode.Light, 0x10FF10));

        Assert.Equal(WindowsThemeApplyOutcome.SettingsWindowOpen, outcome);
        Assert.Null(launcher.Started);
    }

    /// <summary>
    /// Undo re-applies the recorded state onto whatever theme is current then.
    /// </summary>
    /// <remarks>
    /// Not onto a file saved earlier. Windows rewrites the applied theme file and deletes the one
    /// it replaced, so a saved path names a file it has since edited or removed — and rebuilding
    /// from the current one keeps whatever the person changed in the meantime.
    /// </remarks>
    [Fact]
    public async Task Undo_asks_Windows_to_choose_the_accent_again_when_it_had_been()
    {
        var state = new FakeState
        {
            Path = WriteSample(),
            ChangesAfterReads = 0,
            Settings = new WindowsThemeSettings(WindowsThemeMode.Light, 0x10FF10),
            Becomes = new WindowsThemeSettings(WindowsThemeMode.Dark, 0x10FF10, IsAccentAutomatic: true),
        };

        var launcher = new FakeLauncher();
        var wanted = new WindowsThemeSettings(WindowsThemeMode.Dark, 0x533222, IsAccentAutomatic: true);

        var outcome = await Build(state, launcher).ApplyAsync(wanted);

        Assert.Equal(WindowsThemeApplyOutcome.Applied, outcome);
        Assert.Contains("AutoColorization=1", Read(launcher.Started!), StringComparison.Ordinal);
    }

    /// <summary>
    /// Asking Windows to choose the accent is confirmed on the setting, not on a colour.
    /// </summary>
    /// <remarks>
    /// There is no colour to compare: Windows picks one. Comparing against the colour that was
    /// requested would fail every undo of this kind, and comparing against nothing would pass every
    /// one of them.
    /// </remarks>
    [Fact]
    public async Task An_automatic_accent_that_stays_switched_off_is_not_confirmed()
    {
        var state = new FakeState
        {
            Path = WriteSample(),
            ChangesAfterReads = 0,
            Becomes = new WindowsThemeSettings(WindowsThemeMode.Dark, 0x533222, IsAccentAutomatic: false),
        };

        var wanted = new WindowsThemeSettings(WindowsThemeMode.Dark, 0x533222, IsAccentAutomatic: true);

        Assert.Equal(
            WindowsThemeApplyOutcome.NotConfirmed,
            await Build(state, new FakeLauncher()).ApplyAsync(wanted));
    }
}
