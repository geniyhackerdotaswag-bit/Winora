using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Whether this copy of the program is the installed one.
/// </summary>
/// <remarks>
/// Everything downstream turns on this answer. Said yes wrongly, the updater would try to replace a
/// file in the Downloads folder that the person never agreed to have replaced. Said no wrongly, an
/// installed copy would keep offering to install itself, over and over, on every launch.
/// </remarks>
public sealed class AppInstallLocationTests
{
    private const string Programs = @"C:\Users\someone\AppData\Local\Programs";

    private static AppInstallLocation At(string current) => new(current, Programs);

    [Fact]
    public void The_installed_place_is_Winora_under_the_programs_folder()
    {
        var location = At(@"C:\Users\someone\Downloads\Winora.exe");

        Assert.Equal(Path.Combine(Programs, "Winora"), location.InstalledDirectory);
        Assert.Equal(Path.Combine(Programs, "Winora", "Winora.exe"), location.InstalledExecutablePath);
    }

    [Fact]
    public void A_copy_in_the_programs_folder_is_installed()
    {
        Assert.True(At(Path.Combine(Programs, "Winora", "Winora.exe")).IsInstalled);
    }

    /// <summary>Case and separators are Windows' business, not a reason to answer differently.</summary>
    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Local\Programs\Winora\WINORA.EXE")]
    [InlineData(@"C:\Users\someone\AppData\Local\Programs\winora\winora.exe")]
    [InlineData(@"C:/Users/someone/AppData/Local/Programs/Winora/Winora.exe")]
    [InlineData(@"C:\Users\someone\AppData\Local\Programs\Winora\..\Winora\Winora.exe")]
    public void The_same_file_written_differently_is_still_installed(string current)
    {
        Assert.True(At(current).IsInstalled);
    }

    [Theory]
    [InlineData(@"C:\Users\someone\Downloads\Winora.exe")]
    [InlineData(@"C:\Users\someone\Desktop\Winora (1).exe")]
    [InlineData(@"C:\Users\someone\AppData\Local\Programs\Winora\Winora.App.exe")]
    [InlineData(@"C:\Users\someone\AppData\Local\Programs\WinoraOld\Winora.exe")]
    public void Anything_else_is_not_installed(string current)
    {
        Assert.False(At(current).IsInstalled);
    }

    /// <summary>
    /// The build output is called Winora.App.exe and the release is called Winora.exe. Keeping the
    /// name part of the answer means a build run from the debugger never counts as installed, and
    /// development never tries to update itself.
    /// </summary>
    [Fact]
    public void The_build_output_name_is_not_the_release_name()
    {
        Assert.False(At(Path.Combine(Programs, "Winora", "Winora.App.exe")).IsInstalled);
    }

    /// <summary>
    /// The parameterless constructor resolves real folders rather than throwing or returning empty.
    /// </summary>
    /// <remarks>
    /// Comparing CurrentExecutablePath against Environment.ProcessPath would restate the line that
    /// produces it and could never fail. What is worth asserting is what the rest of the code
    /// assumes: that the paths come out rooted, under the real local app data folder, and named the
    /// way an installed copy is named.
    /// </remarks>
    [Fact]
    public void The_real_one_resolves_to_real_folders()
    {
        var location = new AppInstallLocation();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.True(Path.IsPathRooted(location.InstalledExecutablePath));
        Assert.StartsWith(localAppData, location.InstalledDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Winora.exe", Path.GetFileName(location.InstalledExecutablePath));
        Assert.Equal(location.InstalledDirectory, Path.GetDirectoryName(location.InstalledExecutablePath));
    }
}
