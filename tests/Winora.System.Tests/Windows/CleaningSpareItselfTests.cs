using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Windows;

/// <summary>
/// Winora's cleanup never takes Winora's own running files.
/// </summary>
/// <remarks>
/// <para>
/// A single-file build is unpacked into the temporary folder before it starts, which puts several
/// hundred of its own files inside the folder its cleanup screen offers to empty. Windows holds
/// only some of them: measured on 2026-08-27 while the program was running, 76 of 549 were locked
/// and the other 473 were free to delete.
/// </para>
/// <para>
/// The damage is delayed, which is what makes it bad. The program keeps working until it reaches
/// for one of the assemblies that load only when a screen first needs them, and the next start
/// finds an incomplete bundle and dies before any of its own code can report anything.
/// </para>
/// </remarks>
public sealed class CleaningSpareItselfTests : IDisposable
{
    private readonly string _root;

    public CleaningSpareItselfTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "winora-spare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temporary folder is not worth failing a test over.
        }
    }

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void The_running_programs_own_folder_is_recognised()
    {
        var mine = Folder(".net", "Winora", "abcd");

        Assert.True(RunningProgramFolder.Holds(mine, mine));
        Assert.True(RunningProgramFolder.Holds(Path.Combine(mine, "DWriteCore.dll"), mine));
        Assert.True(RunningProgramFolder.Holds(Path.Combine(mine, "sub", "deep.dll"), mine));
    }

    /// <summary>
    /// The unpacked copy of another version is not spared.
    /// </summary>
    /// <remarks>
    /// Those are the ones worth clearing — they are what piled up to 1.36 GB on the owner's machine.
    /// Sparing the whole unpacking area to be safe would protect the very thing the cleanup exists
    /// to remove.
    /// </remarks>
    [Fact]
    public void Another_versions_unpacked_copy_is_not_spared()
    {
        var mine = Folder(".net", "Winora", "abcd");
        var replaced = Folder(".net", "Winora", "efgh");

        Assert.False(RunningProgramFolder.Holds(replaced, mine));
        Assert.False(RunningProgramFolder.Holds(Path.Combine(replaced, "Winora.dll"), mine));
    }

    /// <summary>A folder whose name merely starts the same way is a different folder.</summary>
    [Fact]
    public void A_folder_with_a_similar_name_is_not_the_same_folder()
    {
        var mine = Folder(".net", "Winora", "abcd");
        var other = Folder(".net", "Winora", "abcdefgh");

        Assert.False(RunningProgramFolder.Holds(other, mine));
    }

    /// <summary>
    /// Not knowing where the running program's files are spares nothing.
    /// </summary>
    /// <remarks>
    /// The opposite of the usual caution here, and deliberately: an ordinary build is not unpacked
    /// into the temporary folder at all, so there is nothing to protect, and refusing to clean over
    /// a question that went unanswered would leave the screen unable to do its one job.
    /// </remarks>
    [Fact]
    public void Without_knowing_where_the_program_lives_nothing_is_spared()
    {
        Assert.False(RunningProgramFolder.Holds(Folder("anything"), null));
        Assert.False(RunningProgramFolder.Holds(Folder("anything"), string.Empty));
    }

    [Fact]
    public void A_trailing_separator_does_not_change_the_answer()
    {
        var mine = Folder(".net", "Winora", "abcd");

        Assert.True(RunningProgramFolder.Holds(mine + Path.DirectorySeparatorChar, mine));
        Assert.True(RunningProgramFolder.Holds(mine, mine + Path.DirectorySeparatorChar));
    }

    /// <summary>Windows paths differ in case without being different paths.</summary>
    [Fact]
    public void Case_does_not_change_the_answer()
    {
        var mine = Folder(".net", "Winora", "abcd");

        Assert.True(RunningProgramFolder.Holds(mine.ToUpperInvariant(), mine));
    }

    /// <summary>A path that is not a path at all is answered rather than thrown at the caller.</summary>
    [Fact]
    public void Nonsense_is_answered_with_no()
    {
        Assert.False(RunningProgramFolder.Holds("   ", Folder("mine")));
        Assert.False(RunningProgramFolder.Holds(new string('x', 500), Folder("mine")));
    }

    private sealed class NotElevated : IElevationProbe
    {
        public bool IsElevated => false;
    }

    /// <summary>
    /// Cleaning a folder the program lives inside takes everything except the program.
    /// </summary>
    /// <remarks>
    /// The whole point, end to end: the rubbish goes, the running copy stays. Before this, cleaning
    /// the temporary folder deleted most of Winora out from under Winora.
    /// </remarks>
    [Fact]
    public void Cleaning_takes_the_rubbish_and_leaves_the_running_program()
    {
        var mine = Folder(".net", "Winora", "abcd");
        var replaced = Folder(".net", "Winora", "efgh");

        File.WriteAllText(Path.Combine(mine, "DWriteCore.dll"), "the running program");
        File.WriteAllText(Path.Combine(mine, "MainWindow.xaml"), "the running program");
        File.WriteAllText(Path.Combine(replaced, "Winora.dll"), "a version already replaced");
        File.WriteAllText(Path.Combine(_root, "installer-scratch.tmp"), "rubbish");

        var location = new TempLocation("user-temp", _root, TempLocationClassification.UserOwned, null);
        var cleaner = new WindowsTempCleaner(new NotElevated(), () => mine);

        var result = cleaner.Clean(location, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(mine, "DWriteCore.dll")));
        Assert.True(File.Exists(Path.Combine(mine, "MainWindow.xaml")));

        Assert.False(File.Exists(Path.Combine(replaced, "Winora.dll")));
        Assert.False(File.Exists(Path.Combine(_root, "installer-scratch.tmp")));

        Assert.Equal(2, result.DeletedCount);
    }

    /// <summary>
    /// The spared files are not reported as ones something was holding.
    /// </summary>
    /// <remarks>
    /// They were never candidates. Counting them as skipped would put a number on the screen that
    /// says files could not be removed, which is a different statement and not a true one.
    /// </remarks>
    [Fact]
    public void The_spared_files_are_not_counted_as_having_been_in_the_way()
    {
        var mine = Folder(".net", "Winora", "abcd");

        for (var index = 0; index < 5; index++)
        {
            File.WriteAllText(Path.Combine(mine, $"part{index}.dll"), "the running program");
        }

        var location = new TempLocation("user-temp", _root, TempLocationClassification.UserOwned, null);

        var result = new WindowsTempCleaner(new NotElevated(), () => mine)
            .Clean(location, CancellationToken.None);

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.SkippedCount);
    }
}
