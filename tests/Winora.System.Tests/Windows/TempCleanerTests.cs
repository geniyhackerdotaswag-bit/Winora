using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// Operates only inside a temporary directory created per test. Deletion is irreversible, so these
/// tests never point the cleaner at anything the developer owns.
/// </summary>
public sealed class TempCleanerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("winora-cleaner-tests").FullName;

    private TempLocation UserOwned => new("user-temp", _root, TempLocationClassification.UserOwned, null);

    /// <summary>Elevation is a property of the process, so tests state it rather than inherit it.</summary>
    private sealed class FakeElevation(bool isElevated) : IElevationProbe
    {
        public bool IsElevated { get; } = isElevated;
    }

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

    /// <summary>
    /// The guard that matters most. Everything else here is recoverable by re-creating a temp file;
    /// deleting from a Windows-serviced location is not.
    /// </summary>
    [Theory]
    [InlineData(TempLocationClassification.Protected)]
    [InlineData(TempLocationClassification.Unavailable)]
    public void A_location_that_is_not_user_owned_is_refused(TempLocationClassification classification)
    {
        var location = new TempLocation("windows-temp", _root, classification, "winora.cleanup.windows-serviced");

        Assert.Throws<InvalidOperationException>(
            () => new WindowsTempCleaner(new FakeElevation(false)).Clean(location, CancellationToken.None));
    }

    /// <summary>
    /// Windows.old stays refused even to an administrator: there is no documented programmatic
    /// removal and deleting it closes the route back to the previous Windows version for good.
    /// </summary>
    [Fact]
    public void A_protected_location_is_refused_even_when_elevated()
    {
        var location = new TempLocation(
            "windows-old", _root, TempLocationClassification.Protected, "winora.cleanup.previous-installation");

        Assert.Throws<InvalidOperationException>(
            () => new WindowsTempCleaner(new FakeElevation(true)).Clean(location, CancellationToken.None));
    }

    [Fact]
    public void A_windows_serviced_location_is_refused_without_rights_and_allowed_with_them()
    {
        Seed("a.txt", "12345");
        var location = new TempLocation(
            "windows-temp", _root, TempLocationClassification.RequiresElevation, "winora.cleanup.windows-serviced");

        Assert.Throws<InvalidOperationException>(
            () => new WindowsTempCleaner(new FakeElevation(false)).Clean(location, CancellationToken.None));

        var result = new WindowsTempCleaner(new FakeElevation(true)).Clean(location, CancellationToken.None);
        Assert.Equal(1, result.DeletedCount);
    }

    [Fact]
    public void Cleaning_removes_files_and_reports_what_it_freed()
    {
        Seed("a.txt", "12345");
        Seed(Path.Combine("nested", "b.txt"), "678");

        var result = new WindowsTempCleaner(new FakeElevation(false)).Clean(UserOwned, CancellationToken.None);

        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(8, result.DeletedBytes);
        Assert.Equal(0, result.SkippedCount);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Empty_directories_are_removed_but_the_location_itself_survives()
    {
        Seed(Path.Combine("nested", "deeper", "b.txt"), "x");

        new WindowsTempCleaner(new FakeElevation(false)).Clean(UserOwned, CancellationToken.None);

        Assert.True(Directory.Exists(_root));
        Assert.False(Directory.Exists(Path.Combine(_root, "nested")));
    }

    /// <summary>
    /// Temporary directories are in constant use. A cleaner that claimed to empty one would be
    /// lying, so a locked file is counted and reported instead.
    /// </summary>
    [Fact]
    public void A_file_in_use_is_skipped_and_counted()
    {
        Seed("free.txt", "free");
        Seed("locked.txt", "locked");

        using var handle = new FileStream(
            Path.Combine(_root, "locked.txt"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var result = new WindowsTempCleaner(new FakeElevation(false)).Clean(UserOwned, CancellationToken.None);

        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(File.Exists(Path.Combine(_root, "locked.txt")));
    }

    [Fact]
    public void A_read_only_file_is_still_removed()
    {
        Seed("scratch.tmp", "installer leftovers");
        File.SetAttributes(Path.Combine(_root, "scratch.tmp"), FileAttributes.ReadOnly);

        var result = new WindowsTempCleaner(new FakeElevation(false)).Clean(UserOwned, CancellationToken.None);

        Assert.Equal(1, result.DeletedCount);
        Assert.False(File.Exists(Path.Combine(_root, "scratch.tmp")));
    }

    [Fact]
    public void A_location_that_does_not_exist_is_reported_as_nothing_to_do()
    {
        var missing = new TempLocation(
            "user-temp",
            Path.Combine(_root, "not-there"),
            TempLocationClassification.UserOwned,
            null);

        var result = new WindowsTempCleaner(new FakeElevation(false)).Clean(missing, CancellationToken.None);

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.SkippedCount);
    }

    private void Seed(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
