using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Putting a new file where the running one is.
/// </summary>
/// <remarks>
/// Windows will not let a running program be deleted or overwritten, but it will let it be renamed.
/// That single permission is what makes this possible without a second program to do the work, and
/// the order below is arranged around it: nothing is destroyed until the rename has already
/// succeeded, and the step after it is reversible because the renamed file is still a working
/// program.
/// </remarks>
public sealed class AppFileSwapTests : IDisposable
{
    private readonly string _folder;
    private readonly string _target;
    private readonly string _fresh;

    public AppFileSwapTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "winora-swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _target = Path.Combine(_folder, "Winora.exe");
        _fresh = Path.Combine(_folder, "Winora.exe.new");

        File.WriteAllText(_target, "old program");
        File.WriteAllText(_fresh, "new program");
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

    [Fact]
    public void The_new_file_takes_the_place_of_the_old_one()
    {
        Assert.Equal(SwapResult.Replaced, AppFileSwap.Replace(_target, _fresh));

        Assert.Equal("new program", File.ReadAllText(_target));
        Assert.False(File.Exists(_fresh));
    }

    /// <summary>The displaced program is kept, not deleted: until the new one runs it is the fallback.</summary>
    [Fact]
    public void The_old_program_is_set_aside_rather_than_destroyed()
    {
        AppFileSwap.Replace(_target, _fresh);

        Assert.Equal("old program", File.ReadAllText(_target + AppFileSwap.OldSuffix));
    }

    /// <summary>A leftover from a previous update must not stop the next one.</summary>
    [Fact]
    public void A_leftover_from_last_time_does_not_block_the_swap()
    {
        File.WriteAllText(_target + AppFileSwap.OldSuffix, "from last time");

        Assert.Equal(SwapResult.Replaced, AppFileSwap.Replace(_target, _fresh));
        Assert.Equal("new program", File.ReadAllText(_target));
    }

    /// <summary>
    /// Nothing is touched when there is nothing to put in place. Said the other way: a failure
    /// before the rename must leave a working program where it was.
    /// </summary>
    [Fact]
    public void Without_a_new_file_the_old_one_stays_exactly_where_it_was()
    {
        File.Delete(_fresh);

        Assert.Equal(SwapResult.Unchanged, AppFileSwap.Replace(_target, _fresh));
        Assert.Equal("old program", File.ReadAllText(_target));
        Assert.False(File.Exists(_target + AppFileSwap.OldSuffix));
    }

    /// <summary>
    /// The step the whole order exists for: if putting the new file in place fails, the working
    /// program comes back.
    /// </summary>
    /// <remarks>
    /// Forced by holding the downloaded file open with no sharing, which is what an antivirus
    /// scanning it at that exact moment looks like from here. The rename of the target has already
    /// happened by then, so this is the one window where the program is not where it belongs, and
    /// it must not be left that way.
    /// </remarks>
    [Fact]
    public void A_swap_that_fails_puts_the_working_program_back()
    {
        using (var held = new FileStream(_fresh, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(SwapResult.Unchanged, AppFileSwap.Replace(_target, _fresh));
        }

        Assert.True(File.Exists(_target));
        Assert.Equal("old program", File.ReadAllText(_target));
        Assert.False(File.Exists(_target + AppFileSwap.OldSuffix));
    }

    [Fact]
    public void Leftovers_are_cleared_away()
    {
        File.WriteAllText(Path.Combine(_folder, "Winora.exe.old"), "gone");
        File.WriteAllText(Path.Combine(_folder, "Winora.exe.new"), "gone too");

        AppFileSwap.RemoveLeftovers(_folder);

        Assert.False(File.Exists(Path.Combine(_folder, "Winora.exe.old")));
        Assert.False(File.Exists(Path.Combine(_folder, "Winora.exe.new")));
        Assert.True(File.Exists(_target));
    }

    /// <summary>
    /// A leftover that cannot be removed is not a reason to fail. It is removed next time, and a
    /// program that refused to start because of a stale file would be worse than the stale file.
    /// </summary>
    [Fact]
    public void A_leftover_that_will_not_go_is_not_an_error()
    {
        var stuck = Path.Combine(_folder, "Winora.exe.old");
        File.WriteAllText(stuck, "held open");

        using var hold = new FileStream(stuck, FileMode.Open, FileAccess.Read, FileShare.None);

        AppFileSwap.RemoveLeftovers(_folder);

        Assert.True(File.Exists(stuck));
    }

    [Fact]
    public void Clearing_a_folder_that_is_not_there_is_not_an_error()
    {
        AppFileSwap.RemoveLeftovers(Path.Combine(_folder, "absent"));
    }

    /// <summary>
    /// A leftover .old file that cannot be deleted blocks the next swap, leaving the target
    /// untouched. The result must be Unchanged, not Replaced, because nothing was moved.
    /// </summary>
    /// <remarks>
    /// If TryDelete(displaced) fails because the file is locked, then File.Move(target, displaced)
    /// fails because the destination exists. This prevents any change, so Unchanged is correct.
    /// </remarks>
    [Fact]
    public void A_stuck_leftover_prevents_the_swap()
    {
        var displaced = _target + AppFileSwap.OldSuffix;
        File.WriteAllText(displaced, "held open");

        using var hold = new FileStream(displaced, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = AppFileSwap.Replace(_target, _fresh);

        // Nothing was moved because we could not delete the leftover.
        Assert.Equal(SwapResult.Unchanged, result);
        // The target is still the old program.
        Assert.Equal("old program", File.ReadAllText(_target));
        // The fresh file was never touched.
        Assert.Equal("new program", File.ReadAllText(_fresh));
        // The stuck leftover is still there.
        Assert.True(File.Exists(displaced));
    }

    /// <summary>
    /// Both moves fail after the program has already been set aside: it is gone from its own path,
    /// and the caller has to be told that rather than "nothing happened".
    /// </summary>
    /// <remarks>
    /// Reached through the internal seam because it cannot be reached any other way — by the time
    /// the rescue runs, the file it needs was created by a rename that succeeded a moment earlier,
    /// so no lock taken before the call can still be on it. The first move is allowed to really
    /// happen: what is being pinned is the state it leaves behind.
    /// </remarks>
    [Fact]
    public void When_the_rescue_also_fails_the_program_is_reported_as_displaced()
    {
        var moves = 0;

        var result = AppFileSwap.Replace(_target, _fresh, (from, to) =>
        {
            moves++;
            if (moves == 1)
            {
                File.Move(from, to);
                return;
            }

            throw new IOException("held open");
        });

        Assert.Equal(SwapResult.Displaced, result);
        Assert.False(File.Exists(_target));
        Assert.Equal("old program", File.ReadAllText(_target + AppFileSwap.OldSuffix));
    }
}
