using Winora.Infrastructure.Backups;
using Winora.Infrastructure.Paths;
using Xunit;

namespace Winora.Infrastructure.Tests.Backups;

/// <summary>
/// Operates entirely inside a temporary directory created per test, so the developer's own files
/// are never involved.
/// </summary>
public sealed class QuarantineStoreTests : IDisposable
{
    private const string OperationId = "winora-cleanup-user-temp";

    private readonly string _root = Directory.CreateTempSubdirectory("winora-quarantine-tests").FullName;

    private string Source => Path.Combine(_root, "source");

    private QuarantineStore Store => new(WinoraQuarantinePaths.ForRoot(Path.Combine(_root, "winora")));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public void Moving_relocates_files_and_reports_what_it_took()
    {
        Seed("a.txt", "one");
        Seed(Path.Combine("nested", "b.txt"), "two");

        var result = Store.Move(OperationId, Source, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(0, result.SkippedCount);
        Assert.False(File.Exists(Path.Combine(Source, "a.txt")));
        Assert.True(File.Exists(Path.Combine(Store.DirectoryFor(OperationId), "a.txt")));
        Assert.True(File.Exists(Path.Combine(Store.DirectoryFor(OperationId), "nested", "b.txt")));
    }

    [Fact]
    public void The_relative_layout_is_preserved_so_restore_puts_things_back_exactly()
    {
        Seed(Path.Combine("deep", "deeper", "c.txt"), "three");
        var store = Store;
        store.Move(OperationId, Source, CancellationToken.None);

        var result = store.Restore(OperationId, Source, CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal("three", File.ReadAllText(Path.Combine(Source, "deep", "deeper", "c.txt")));
        Assert.True(store.IsEmpty(OperationId));
    }

    [Fact]
    public void Restoring_twice_is_harmless()
    {
        Seed("a.txt", "one");
        var store = Store;
        store.Move(OperationId, Source, CancellationToken.None);
        store.Restore(OperationId, Source, CancellationToken.None);

        var second = store.Restore(OperationId, Source, CancellationToken.None);

        Assert.Empty(second.Items);
        Assert.True(File.Exists(Path.Combine(Source, "a.txt")));
    }

    [Fact]
    public void Restoring_an_operation_that_never_quarantined_anything_is_not_an_error()
    {
        Directory.CreateDirectory(Source);

        var result = Store.Restore(OperationId, Source, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.SkippedCount);
    }

    /// <summary>
    /// A file held open is left where it is and counted, so the caller reports what was actually
    /// reclaimed rather than implying the location was emptied.
    /// </summary>
    [Fact]
    public void A_file_in_use_is_skipped_and_counted_rather_than_failing_the_whole_move()
    {
        Seed("free.txt", "free");
        Seed("locked.txt", "locked");

        using var handle = new FileStream(
            Path.Combine(Source, "locked.txt"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var result = Store.Move(OperationId, Source, CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(File.Exists(Path.Combine(Source, "locked.txt")));
    }

    [Fact]
    public void Recorded_identity_matches_the_file_that_moved()
    {
        Seed("a.txt", "exactly eleven");

        var item = Store.Move(OperationId, Source, CancellationToken.None).Items.Single();

        Assert.Equal("a.txt", item.RelativePath);
        Assert.Equal("exactly eleven".Length, item.Length);
    }

    [Fact]
    public void A_source_on_another_volume_is_refused_rather_than_copied()
    {
        Seed("a.txt", "one");
        var store = new QuarantineStore(WinoraQuarantinePaths.ForRoot(@"\\?\Z:\winora"));

        Assert.Throws<InvalidOperationException>(
            () => store.Move(OperationId, Source, CancellationToken.None));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData(@"..\escape.txt")]
    [InlineData(@"nested\..\..\escape.txt")]
    public void A_relative_path_that_would_leave_the_operation_directory_is_refused(string relativePath)
    {
        var paths = WinoraQuarantinePaths.ForRoot(Path.Combine(_root, "winora"));

        Assert.Throws<ArgumentException>(() => paths.ResolveOwnedPath(OperationId, relativePath));
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("has space")]
    [InlineData("con")]
    [InlineData("nul.json")]
    public void An_unsafe_operation_directory_name_is_refused(string operationId)
    {
        var paths = WinoraQuarantinePaths.ForRoot(Path.Combine(_root, "winora"));

        Assert.Throws<ArgumentException>(() => paths.DirectoryFor(operationId));
    }

    [Fact]
    public void The_quarantine_never_lives_under_local_app_data()
    {
        // Package storage under %LOCALAPPDATA% is deleted when the package is uninstalled, and the
        // quarantine holds the user's own files.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var actual = WinoraQuarantinePaths.ForCurrentUser().QuarantineDirectory;

        Assert.DoesNotContain(localAppData, actual, StringComparison.OrdinalIgnoreCase);
    }

    private void Seed(string relativePath, string content)
    {
        var full = Path.Combine(Source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
