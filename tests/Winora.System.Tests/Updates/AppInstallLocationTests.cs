using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Where this copy of the program is, and whether it may replace itself there.
/// </summary>
/// <remarks>
/// Winora is portable: it lives where it was put. Until 2026-09-03 it treated only a copy inside
/// <c>%LOCALAPPDATA%\Programs\Winora</c> as real, offered on every launch to move itself there, and
/// refused self-update to any copy running from anywhere else. The owner had that removed.
///
/// Everything downstream still turns on this answer: said no wrongly, the updater sends the user to
/// a web page instead of updating the copy in front of them.
/// </remarks>
public sealed class AppInstallLocationTests
{
    [Theory]
    [InlineData(@"C:\Users\someone\Downloads\Winora.exe", @"C:\Users\someone\Downloads")]
    [InlineData(@"D:\Портативные\Winora\Winora.exe", @"D:\Портативные\Winora")]
    [InlineData(@"C:\Users\someone\AppData\Local\Programs\Winora\Winora.exe",
        @"C:\Users\someone\AppData\Local\Programs\Winora")]
    public void The_program_lives_in_the_folder_it_was_started_from(string current, string expected)
    {
        var location = new AppInstallLocation(current);

        Assert.Equal(expected, location.InstalledDirectory);
        Assert.Equal(Path.Combine(expected, "Winora.exe"), location.InstalledExecutablePath);
    }

    /// <summary>A released copy may replace itself wherever it stands — that is the whole change.</summary>
    [Theory]
    [InlineData(@"C:\Users\someone\Downloads\Winora.exe")]
    [InlineData(@"D:\Портативные\Winora\Winora.exe")]
    [InlineData(@"C:\Users\someone\Desktop\Winora.exe")]
    public void A_released_copy_updates_itself_wherever_it_is(string current)
    {
        Assert.True(new AppInstallLocation(current).IsInstalled);
    }

    /// <summary>Case and separators are Windows' business, not a reason to answer differently.</summary>
    [Theory]
    [InlineData(@"C:\Users\someone\Downloads\WINORA.EXE")]
    [InlineData(@"C:/Users/someone/Downloads/Winora.exe")]
    [InlineData(@"C:\Users\someone\Downloads\..\Downloads\Winora.exe")]
    public void The_same_file_written_differently_is_still_the_same_file(string current)
    {
        Assert.True(new AppInstallLocation(current).IsInstalled);
    }

    /// <summary>
    /// The build output is called Winora.App.exe and the release is called Winora.exe.
    /// </summary>
    /// <remarks>
    /// Keeping the name part of the answer is what stops a build run from the debugger from
    /// updating itself out from under the debugger. It is the only reason the check is not simply
    /// "yes".
    /// </remarks>
    [Fact]
    public void The_build_output_name_is_not_the_release_name()
    {
        Assert.False(new AppInstallLocation(@"C:\src\bin\Winora.App.exe").IsInstalled);
    }

    /// <summary>A path that cannot be read as one answers empty rather than throwing.</summary>
    /// <remarks>
    /// This is read in the first lines of startup, before there is a window to show a failure in.
    /// A throw here would be a program that never opens and never says why — which is exactly what
    /// shipped as 0.9.2 and had to be withdrawn.
    /// </remarks>
    [Fact]
    public void An_unusable_path_is_empty_and_not_installed()
    {
        var location = new AppInstallLocation(string.Empty);

        Assert.Equal(string.Empty, location.InstalledDirectory);
        Assert.Equal(string.Empty, location.InstalledExecutablePath);
        Assert.False(location.IsInstalled);
    }

    /// <summary>The parameterless constructor resolves real folders rather than throwing.</summary>
    [Fact]
    public void The_real_one_resolves_to_a_real_folder()
    {
        var location = new AppInstallLocation();

        Assert.True(Path.IsPathRooted(location.InstalledExecutablePath));
        Assert.Equal("Winora.exe", Path.GetFileName(location.InstalledExecutablePath));
        Assert.Equal(location.InstalledDirectory, Path.GetDirectoryName(location.InstalledExecutablePath));
    }
}
