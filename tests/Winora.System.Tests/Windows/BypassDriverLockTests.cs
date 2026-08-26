using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Windows;

/// <summary>
/// Telling a loaded driver apart from an ordinary locked file.
/// </summary>
/// <remarks>
/// The two need different advice. For an ordinary lock, "stop what is running and try again" is
/// right. For a driver it is wrong: stopping winws.exe does not unload WinDivert, and somebody
/// following that advice repeats the same failure for as long as they are willing to.
/// </remarks>
public sealed class BypassDriverLockTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "winora-driver-" + Guid.NewGuid().ToString("N"));

    public BypassDriverLockTests() => Directory.CreateDirectory(Path.Combine(_folder, "bin"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A temporary folder that outlives the run is not worth failing a test over.
        }
    }

    private string Write(string relative)
    {
        var path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void A_folder_nobody_is_holding_reports_no_driver()
    {
        Write(@"bin\WinDivert64.sys");
        Write(@"bin\winws.exe");

        Assert.Null(BypassReleaseInstaller.LoadedDriver(_folder));
    }

    [Fact]
    public void A_folder_that_is_not_there_reports_no_driver()
    {
        Assert.Null(BypassReleaseInstaller.LoadedDriver(Path.Combine(_folder, "missing")));
    }

    /// <summary>A held driver file is what the kernel looks like from here.</summary>
    [Fact]
    public void A_held_driver_file_is_found_and_named()
    {
        var driver = Write(@"bin\WinDivert64.sys");

        using var held = File.Open(driver, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal("WinDivert64.sys", BypassReleaseInstaller.LoadedDriver(_folder));
    }

    /// <summary>
    /// An ordinary file being held is an ordinary lock, and gets the ordinary advice.
    /// </summary>
    /// <remarks>
    /// Restricting the search to .sys is the whole of the distinction. Without it a running
    /// winws.exe would be reported as a loaded driver, and the person would be sent to unload
    /// something that is not loaded.
    /// </remarks>
    [Fact]
    public void A_held_executable_is_not_a_driver()
    {
        var executable = Write(@"bin\winws.exe");
        Write(@"bin\WinDivert64.sys");

        using var held = File.Open(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Null(BypassReleaseInstaller.LoadedDriver(_folder));
    }

    [Fact]
    public void A_driver_deeper_in_the_tree_is_still_found()
    {
        var driver = Write(@"bin\drivers\amd64\WinDivert64.sys");

        using var held = File.Open(driver, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal("WinDivert64.sys", BypassReleaseInstaller.LoadedDriver(_folder));
    }
}
