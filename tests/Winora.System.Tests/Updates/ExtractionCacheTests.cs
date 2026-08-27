using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Clearing away the unpacked copies of versions that have been replaced.
/// </summary>
/// <remarks>
/// .NET unpacks a single-file build into a folder keyed by build, and never removes the old ones.
/// Eight dead versions had accumulated 1.36 GB on the owner's machine, on the drive that then had
/// nothing left for the ninth. The program that made the mess clears it.
/// </remarks>
public sealed class ExtractionCacheTests : IDisposable
{
    private readonly string _temp;
    private readonly string _root;
    private readonly string _folder;

    public ExtractionCacheTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "winora-extract-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_temp, ".net");
        _folder = Path.Combine(_root, "Winora");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temporary folder is not worth failing a test over.
        }
    }

    private string Unpacked(string id)
    {
        var path = Path.Combine(_folder, id);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Winora.dll"), "x");
        return path;
    }

    [Fact]
    public void Copies_of_replaced_versions_go()
    {
        Unpacked("aaaa");
        Unpacked("bbbb");
        var running = Unpacked("cccc");

        Assert.Equal(2, ExtractionCache.RemoveReplaced("Winora", running, _root));
        Assert.Single(Directory.EnumerateDirectories(_folder));
    }

    /// <summary>The copy the running program was unpacked from is never touched.</summary>
    [Fact]
    public void The_copy_in_use_stays()
    {
        var running = Unpacked("cccc");

        ExtractionCache.RemoveReplaced("Winora", running, _root);

        Assert.True(Directory.Exists(running));
    }

    /// <summary>
    /// Not knowing which copy is in use means removing none of them.
    /// </summary>
    /// <remarks>
    /// Deleting the folder underneath a running program is worse than leaving every copy on disk.
    /// Disk space is a nuisance; a program losing the files it is executing from is not.
    /// </remarks>
    [Fact]
    public void Without_knowing_which_copy_is_running_nothing_is_removed()
    {
        Unpacked("aaaa");
        Unpacked("bbbb");

        Assert.Equal(0, ExtractionCache.RemoveReplaced("Winora", null, _root));
        Assert.Equal(2, Directory.EnumerateDirectories(_folder).Count());
    }

    /// <summary>
    /// A copy another running program is holding stays, and the rest still go.
    /// </summary>
    /// <remarks>
    /// A second Winora may be running from one of these. There is no list to consult: the operating
    /// system refusing to delete the file is the answer, and it is a better one than any guess.
    /// </remarks>
    [Fact]
    public void A_copy_another_program_is_holding_stays_and_the_others_still_go()
    {
        var held = Unpacked("aaaa");
        Unpacked("bbbb");
        var running = Unpacked("cccc");

        using var handle = File.Open(
            Path.Combine(held, "Winora.dll"),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.Equal(1, ExtractionCache.RemoveReplaced("Winora", running, _root));
        Assert.True(Directory.Exists(held));
    }

    [Fact]
    public void Nothing_unpacked_yet_is_not_a_failure()
    {
        Directory.Delete(_folder);

        Assert.Equal(0, ExtractionCache.RemoveReplaced("Winora", Path.Combine(_folder, "cccc"), _root));
    }

    /// <summary>Only Winora's own copies, never another program's.</summary>
    [Fact]
    public void Another_programs_unpacked_copies_are_left_alone()
    {
        var other = Path.Combine(_root, "SomethingElse", "aaaa");
        Directory.CreateDirectory(other);

        ExtractionCache.RemoveReplaced("Winora", Unpacked("cccc"), _root);

        Assert.True(Directory.Exists(other));
    }
}
