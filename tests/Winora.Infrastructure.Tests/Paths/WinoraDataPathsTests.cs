using Winora.Infrastructure.Paths;
using Xunit;

namespace Winora.Infrastructure.Tests.Paths;

public sealed class WinoraDataPathsTests
{
    /// <summary>
    /// The store lives beside the program.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Winora is portable, and the owner asked on 2026-09-03 for everything it needs to sit in the
    /// folder they put it in. Move that folder and the journal, the backups and the profile go with
    /// it.
    /// </para>
    /// <para>
    /// The test host is not a single-file Winora, so the folder it names here is the test runner's.
    /// That is the point: what is asserted is the rule — beside the running program, under
    /// <c>WinoraData</c> — and not a path that only holds on one machine.
    /// </para>
    /// <para>
    /// This test used to pin <c>LocalApplicationData</c>, and in doing so it pinned a real defect: a
    /// packaged app has that folder redirected into its own container, and Windows deletes the
    /// container when the package is removed. Measured on 2026-08-04 — an uninstall took the
    /// journal, the plan archive and every backup with it, while the registry changes those backups
    /// existed to undo stayed applied. Neither of the two homes since then is redirected.
    /// </para>
    /// </remarks>
    [Fact]
    public void Current_user_root_sits_beside_the_running_program()
    {
        var beside = WinoraDataPaths.BesideTheProgram();

        Assert.False(string.IsNullOrEmpty(beside), "The program's own folder could not be resolved.");
        Assert.Equal(WinoraDataPaths.StoreFolderName, Path.GetFileName(beside));
        Assert.Equal(
            Path.GetDirectoryName(Path.GetFullPath(Environment.ProcessPath!)),
            Path.GetDirectoryName(beside));

        // Resolved, not assumed: a folder that refuses writes sends the store to the profile, and
        // asserting the beside-path unconditionally would fail on exactly those machines.
        Assert.Equal(
            Path.GetFullPath(WinoraDataPaths.RootForCurrentUser()),
            WinoraDataPaths.ForCurrentUser().RootDirectory);
    }

    /// <summary>
    /// The requirement behind that path, stated on its own so a later move cannot quietly put the
    /// store back inside the container.
    /// </summary>
    [Fact]
    public void Current_user_root_is_not_inside_app_data()
    {
        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        Assert.DoesNotContain(
            appData,
            WinoraDataPaths.ForCurrentUser().RootDirectory,
            StringComparison.OrdinalIgnoreCase);
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
    [InlineData("OPERATION")]
    [InlineData("Operation-Id")]
    [InlineData("COM¹")]
    [InlineData("COM²")]
    [InlineData("COM³")]
    [InlineData("LPT¹")]
    [InlineData("LPT²")]
    [InlineData("LPT³")]
    public void Dynamic_identifiers_reject_path_traversal(string identifier)
    {
        var paths = CreatePaths();

        Assert.Throws<ArgumentException>(() => paths.GetOperationDirectory(identifier));
    }

    [Fact]
    public void Fixed_layout_factories_encode_authority_path_and_expected_document_identity()
    {
        var paths = CreatePaths();

        var operationEvent = paths.GetOperationTransitionDocument(
            "operation-id",
            7,
            "transition-id");
        var operationProjection = paths.GetOperationManifestDocument("operation-id");

        Assert.Equal(
            Path.Combine(
                paths.OperationsDirectory,
                "operation-id",
                "Transitions",
                "7-transition-id.json"),
            operationEvent.FilePath);
        Assert.Equal("transition-id", operationEvent.DocumentId);
        Assert.Equal(
            Path.Combine(paths.OperationsDirectory, "operation-id", "manifest.json"),
            operationProjection.FilePath);
        Assert.Equal("operation-id", operationProjection.DocumentId);
        Assert.False(typeof(AuthoritativeJsonDestination).GetConstructors().Any());
        Assert.False(typeof(ProjectionJsonDestination).GetConstructors().Any());
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
