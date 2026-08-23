using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Moving a downloaded copy into the place it belongs.
/// </summary>
/// <remarks>
/// The shortcut is written through an interface so this can be checked without leaving anything in
/// the real Start menu. What COM does with the file it is handed is COM's business; what matters
/// here is that a shortcut is asked for, that it points at the copy rather than at the download, and
/// that failing to write one does not fail the installation — a program in the right place without a
/// shortcut is still usable, and refusing to install over a missing menu entry would not be.
/// </remarks>
public sealed class AppInstallerTests : IDisposable
{
    private readonly string _folder;
    private readonly string _downloaded;
    private readonly string _programs;

    public AppInstallerTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "winora-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_folder, "Downloads"));
        _programs = Path.Combine(_folder, "Programs");
        _downloaded = Path.Combine(_folder, "Downloads", "Winora.exe");
        File.WriteAllText(_downloaded, "the downloaded program");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a passing test over.
        }
    }

    private sealed class RecordingShortcuts : IShortcutWriter
    {
        public string? ShortcutPath { get; private set; }

        public string? TargetPath { get; private set; }

        public bool Succeed { get; init; } = true;

        public bool Write(string shortcutPath, string targetPath, string description)
        {
            ShortcutPath = shortcutPath;
            TargetPath = targetPath;
            return Succeed;
        }
    }

    private AppInstaller Installer(RecordingShortcuts shortcuts, string? current = null) =>
        new(
            new AppInstallLocation(current ?? _downloaded, _programs),
            shortcuts,
            Path.Combine(_folder, "StartMenu"));

    [Fact]
    public void A_downloaded_copy_needs_installing()
    {
        Assert.True(Installer(new RecordingShortcuts()).NeedsInstalling);
    }

    [Fact]
    public void The_installed_copy_does_not_need_installing_again()
    {
        var installed = Path.Combine(_programs, "Winora", "Winora.exe");

        Assert.False(Installer(new RecordingShortcuts(), installed).NeedsInstalling);
    }

    [Fact]
    public void Installing_puts_the_program_where_it_belongs()
    {
        var installer = Installer(new RecordingShortcuts());

        Assert.Equal(InstallOutcome.Installed, installer.Install());

        var landed = Path.Combine(_programs, "Winora", "Winora.exe");
        Assert.True(File.Exists(landed));
        Assert.Equal("the downloaded program", File.ReadAllText(landed));
    }

    /// <summary>The download stays. It is the person's file, and deleting it was not asked for.</summary>
    [Fact]
    public void The_downloaded_file_is_left_alone()
    {
        Installer(new RecordingShortcuts()).Install();

        Assert.True(File.Exists(_downloaded));
    }

    [Fact]
    public void A_shortcut_is_asked_for_and_points_at_the_installed_copy()
    {
        var shortcuts = new RecordingShortcuts();

        Installer(shortcuts).Install();

        Assert.Equal(Path.Combine(_programs, "Winora", "Winora.exe"), shortcuts.TargetPath);
        Assert.Equal(Path.Combine(_folder, "StartMenu", "Winora.lnk"), shortcuts.ShortcutPath);
    }

    /// <summary>
    /// A program in the right place with no menu entry is still a working program. Refusing to
    /// install over a shortcut that would not write would be trading something for nothing.
    /// </summary>
    [Fact]
    public void A_shortcut_that_will_not_write_does_not_fail_the_installation()
    {
        var installer = Installer(new RecordingShortcuts { Succeed = false });

        Assert.Equal(InstallOutcome.Installed, installer.Install());
        Assert.True(File.Exists(Path.Combine(_programs, "Winora", "Winora.exe")));
    }

    /// <summary>Installing over an existing copy replaces it rather than refusing.</summary>
    [Fact]
    public void An_existing_copy_is_overwritten()
    {
        var landed = Path.Combine(_programs, "Winora", "Winora.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(landed)!);
        File.WriteAllText(landed, "an older copy");

        Installer(new RecordingShortcuts()).Install();

        Assert.Equal("the downloaded program", File.ReadAllText(landed));
    }

    [Fact]
    public void The_destination_is_reported_before_anything_happens()
    {
        Assert.Equal(
            Path.Combine(_programs, "Winora", "Winora.exe"),
            Installer(new RecordingShortcuts()).DestinationPath);
    }
}
