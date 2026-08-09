using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Tests.Persistence;
using Xunit;

namespace Winora.Infrastructure.Tests.Paths;

/// <summary>
/// Moving the store out of package-local storage.
/// </summary>
/// <remarks>
/// This runs against the journal, the plan archive and every backup at once, so the failure modes
/// matter more than the happy path: a half-moved store, or one silently merged over a newer one,
/// would destroy the only means of undoing changes already made to Windows.
/// </remarks>
public sealed class WinoraStoreMigrationTests
{
    [Fact]
    public void A_store_at_the_old_location_is_moved()
    {
        using var legacy = new TemporaryDirectory();
        using var parent = new TemporaryDirectory();
        var current = Path.Combine(parent.Path, "State");
        Write(legacy.Path, "Operations/one/plan.json", "plan");

        var outcome = new WinoraStoreMigration(legacy.Path, current).Run();

        Assert.Equal(StoreMigrationOutcome.Moved, outcome);
        Assert.Equal("plan", File.ReadAllText(Path.Combine(current, "Operations", "one", "plan.json")));
        Assert.False(Directory.Exists(legacy.Path));
    }

    [Fact]
    public void Nothing_happens_when_the_old_location_is_absent()
    {
        using var parent = new TemporaryDirectory();
        var legacy = Path.Combine(parent.Path, "absent");
        var current = Path.Combine(parent.Path, "State");

        Assert.Equal(StoreMigrationOutcome.NothingToMove, new WinoraStoreMigration(legacy, current).Run());
        Assert.False(Directory.Exists(current));
    }

    /// <summary>
    /// Earlier versions leave an empty directory behind. Treating that as data would block the
    /// migration forever, and the real store would stay where the uninstaller can delete it.
    /// </summary>
    [Fact]
    public void An_empty_old_location_does_not_count_as_data()
    {
        using var legacy = new TemporaryDirectory();
        using var parent = new TemporaryDirectory();
        var current = Path.Combine(parent.Path, "State");

        Assert.Equal(StoreMigrationOutcome.NothingToMove, new WinoraStoreMigration(legacy.Path, current).Run());
    }

    /// <summary>
    /// The live store wins. Pouring an older copy on top of it could resurrect operations that were
    /// already retired, and the user would see changes reappear that they had undone.
    /// </summary>
    [Fact]
    public void A_new_location_that_already_holds_data_is_never_overwritten()
    {
        using var legacy = new TemporaryDirectory();
        using var current = new TemporaryDirectory();
        Write(legacy.Path, "Data/app-settings.json", "old");
        Write(current.Path, "Data/app-settings.json", "live");

        var outcome = new WinoraStoreMigration(legacy.Path, current.Path).Run();

        Assert.Equal(StoreMigrationOutcome.AlreadyMigrated, outcome);
        Assert.Equal("live", File.ReadAllText(Path.Combine(current.Path, "Data", "app-settings.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(legacy.Path, "Data", "app-settings.json")));
    }

    /// <summary>An empty directory at the destination must not stop the move.</summary>
    [Fact]
    public void An_empty_new_location_does_not_block_the_move()
    {
        using var legacy = new TemporaryDirectory();
        using var current = new TemporaryDirectory();
        Write(legacy.Path, "Backups/one/manifest.json", "backup");

        Assert.Equal(StoreMigrationOutcome.Moved, new WinoraStoreMigration(legacy.Path, current.Path).Run());
        Assert.True(File.Exists(Path.Combine(current.Path, "Backups", "one", "manifest.json")));
    }

    /// <summary>Running twice must be harmless: the second pass has nothing left to find.</summary>
    [Fact]
    public void Running_again_after_a_move_does_nothing()
    {
        using var legacy = new TemporaryDirectory();
        using var parent = new TemporaryDirectory();
        var current = Path.Combine(parent.Path, "State");
        Write(legacy.Path, "Journal/index.json", "journal");

        var migration = new WinoraStoreMigration(legacy.Path, current);

        Assert.Equal(StoreMigrationOutcome.Moved, migration.Run());
        Assert.Equal(StoreMigrationOutcome.NothingToMove, migration.Run());
        Assert.Equal("journal", File.ReadAllText(Path.Combine(current, "Journal", "index.json")));
    }

    [Fact]
    public void The_whole_tree_comes_across_not_just_the_top_level()
    {
        using var legacy = new TemporaryDirectory();
        using var parent = new TemporaryDirectory();
        var current = Path.Combine(parent.Path, "State");

        Write(legacy.Path, "Operations/abc/Transitions/1.json", "one");
        Write(legacy.Path, "Operations/abc/Transitions/2.json", "two");
        Write(legacy.Path, "Backups/xyz/payload.bin", "bytes");
        Write(legacy.Path, "Journal/Events/e.json", "event");

        new WinoraStoreMigration(legacy.Path, current).Run();

        Assert.Equal("one", File.ReadAllText(Path.Combine(current, "Operations", "abc", "Transitions", "1.json")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(current, "Operations", "abc", "Transitions", "2.json")));
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(current, "Backups", "xyz", "payload.bin")));
        Assert.Equal("event", File.ReadAllText(Path.Combine(current, "Journal", "Events", "e.json")));
    }

    [Fact]
    public void The_two_locations_must_differ()
    {
        using var directory = new TemporaryDirectory();

        Assert.Throws<ArgumentException>(() => new WinoraStoreMigration(directory.Path, directory.Path));
    }

    /// <summary>The real pair must not be the same folder, or the migration could never run.</summary>
    [Fact]
    public void The_real_locations_are_not_the_same_folder()
    {
        Assert.NotEqual(
            WinoraStoreMigration.LegacyRootForCurrentUser(),
            WinoraDataPaths.RootForCurrentUser(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The new location must sit outside <c>AppData</c>, which is the whole point: a packaged app
    /// has that redirected into a container the uninstaller deletes.
    /// </summary>
    [Fact]
    public void The_new_location_is_outside_app_data()
    {
        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        Assert.DoesNotContain(appData, WinoraDataPaths.RootForCurrentUser(), StringComparison.OrdinalIgnoreCase);
    }

    private static void Write(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
