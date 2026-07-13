using Winora.Infrastructure.Paths;
using Xunit;

namespace Winora.Infrastructure.Tests.Paths;

public sealed class WinoraDataPathsTests
{
    [Fact]
    public void Current_user_root_is_local_application_data_winora()
    {
        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "Winora");

        Assert.Equal(
            Path.GetFullPath(expectedRoot),
            WinoraDataPaths.ForCurrentUser().RootDirectory);
    }

    [Fact]
    public void Injected_root_produces_the_fixed_storage_layout()
    {
        var root = Path.Combine(Path.GetTempPath(), "Winora.Tests", Guid.NewGuid().ToString("N"));

        var paths = new WinoraDataPaths(root);

        Assert.Equal(Path.GetFullPath(root), paths.RootDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Data"), paths.DataDirectory);
        Assert.Equal(Path.Combine(paths.DataDirectory, "app-settings.json"), paths.AppSettingsFile);
        Assert.Equal(Path.Combine(paths.DataDirectory, "change-index.json"), paths.ChangeIndexFile);
        Assert.Equal(Path.Combine(paths.DataDirectory, "recovery-index.json"), paths.RecoveryIndexFile);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Backups"), paths.BackupsDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Operations"), paths.OperationsDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Journal"), paths.JournalDirectory);
        Assert.Equal(Path.Combine(paths.JournalDirectory, "index.json"), paths.JournalIndexFile);
        Assert.Equal(Path.Combine(paths.JournalDirectory, "Events"), paths.JournalEventsDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Assets"), paths.AssetsDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Pending"), paths.PendingDirectory);
    }

    [Fact]
    public void Dynamic_storage_paths_remain_under_the_fixed_root()
    {
        var paths = CreatePaths();

        Assert.Equal(
            Path.Combine(paths.BackupsDirectory, "backup-id"),
            paths.GetBackupDirectory("backup-id"));
        Assert.Equal(
            Path.Combine(paths.OperationsDirectory, "operation-id", "manifest.json"),
            paths.GetOperationManifestFile("operation-id"));
        Assert.Equal(
            Path.Combine(
                paths.OperationsDirectory,
                "operation-id",
                "Transitions",
                "7-transition-id.json"),
            paths.GetOperationTransitionFile("operation-id", 7, "transition-id"));
        Assert.Equal(
            Path.Combine(paths.JournalEventsDirectory, "event-id.json"),
            paths.GetJournalEventFile("event-id"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("nested/escape")]
    [InlineData("nested\\escape")]
    [InlineData("C:\\escape")]
    [InlineData("CON")]
    [InlineData("NUL.json")]
    [InlineData("event-id.")]
    public void Dynamic_identifiers_reject_path_traversal(string identifier)
    {
        var paths = CreatePaths();

        Assert.Throws<ArgumentException>(() => paths.GetOperationDirectory(identifier));
    }

    [Fact]
    public void Owned_path_validation_rejects_paths_outside_the_fixed_root()
    {
        var paths = CreatePaths();
        var sibling = paths.RootDirectory + "-escape";

        Assert.Throws<ArgumentException>(() => paths.EnsureOwnedFilePath(Path.Combine(sibling, "state.json")));
    }

    private static WinoraDataPaths CreatePaths() =>
        new(Path.Combine(Path.GetTempPath(), "Winora.Tests", Guid.NewGuid().ToString("N")));
}
