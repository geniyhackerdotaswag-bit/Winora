using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Whether an update will fit, asked before anything is downloaded.
/// </summary>
/// <remarks>
/// This exists because of the one failure that cannot report itself. A build that arrives and
/// cannot unpack dies before its own code runs: no window, no message, nothing in Winora's own
/// diagnostics. From the outside the program simply stops opening. Measured on the owner's machine
/// on 2026-08-27, where the only trace was a single line in the Windows event log.
/// </remarks>
public sealed class UpdateDiskSpaceTests
{
    private const long Megabyte = 1024L * 1024;

    /// <summary>
    /// The unpacked copy counts, not just the download.
    /// </summary>
    /// <remarks>
    /// Measured: an 88 MB Winora unpacked to 206 MB, 2.3 times over. A check that asked only for
    /// the download size would have passed on the very machine where this failed.
    /// </remarks>
    [Fact]
    public void What_is_needed_is_more_than_the_file_being_downloaded()
    {
        var download = 88 * Megabyte;

        Assert.True(UpdateDiskSpace.NeededFor(download) > download * 3);
    }

    [Fact]
    public void Room_to_spare_fits()
    {
        var space = UpdateDiskSpace.For(88 * Megabyte, 2048 * Megabyte);

        Assert.True(space.Fits);
        Assert.Equal(0, space.Short);
    }

    /// <summary>The case that actually happened: a drive with nothing left on it.</summary>
    [Fact]
    public void A_full_drive_does_not_fit()
    {
        var space = UpdateDiskSpace.For(88 * Megabyte, 0);

        Assert.False(space.Fits);
        Assert.Equal(space.Needed, space.Short);
    }

    /// <summary>
    /// Room for the download alone is not room for the update.
    /// </summary>
    /// <remarks>
    /// The trap this whole type exists for: everything appears to work, and then the program will
    /// not open.
    /// </remarks>
    [Fact]
    public void Room_for_only_the_download_does_not_fit()
    {
        var download = 88 * Megabyte;

        Assert.False(UpdateDiskSpace.For(download, download + Megabyte).Fits);
    }

    /// <summary>How much is missing, so a person can be told a number rather than a mood.</summary>
    [Fact]
    public void What_is_missing_is_the_difference()
    {
        var space = UpdateDiskSpace.For(100 * Megabyte, 100 * Megabyte);

        Assert.Equal(space.Needed - (100 * Megabyte), space.Short);
    }
}
