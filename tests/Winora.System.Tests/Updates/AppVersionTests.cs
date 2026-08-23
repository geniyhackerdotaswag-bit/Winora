using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Reading a version out of text that came from somewhere else.
/// </summary>
/// <remarks>
/// Two sources feed this and neither is under our control in the same way. The running build
/// supplies AssemblyInformationalVersion, which the SDK may decorate with a commit hash. The feed
/// supplies whatever was typed into a git tag. Both arrive as strings, and the comparison that
/// decides whether to offer an update is only as good as this.
/// </remarks>
public sealed class AppVersionTests
{
    [Theory]
    [InlineData("0.4.0", 0, 4, 0)]
    [InlineData("v0.4.0", 0, 4, 0)]
    [InlineData("V0.4.0", 0, 4, 0)]
    [InlineData("  0.4.0  ", 0, 4, 0)]
    [InlineData("0.4.0+a1b2c3d", 0, 4, 0)]
    [InlineData("0.4.0-beta.1", 0, 4, 0)]
    [InlineData("1.2.3.4", 1, 2, 3)]
    public void A_version_is_read_from_the_text(string text, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), AppVersion.Parse(text));
    }

    /// <summary>
    /// Two components mean the third is zero, not "unspecified".
    /// </summary>
    /// <remarks>
    /// This is the trap. Version treats an absent component as -1, so Version.Parse("0.4") compares
    /// as *less* than Version.Parse("0.4.0"). Someone tagging v0.4 while running a build called
    /// 0.4.0 would be told forever that an update is available, and installing it would change
    /// nothing. Everything is normalised to three numbers so that cannot happen.
    /// </remarks>
    [Fact]
    public void A_missing_third_number_is_zero_and_not_less_than_zero()
    {
        Assert.Equal(new Version(0, 4, 0), AppVersion.Parse("0.4"));
        Assert.Equal(AppVersion.Parse("0.4.0"), AppVersion.Parse("0.4"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("0")]
    [InlineData("release-2026-08")]
    [InlineData("..")]
    public void Text_that_is_not_a_version_reads_as_nothing(string? text)
    {
        Assert.Null(AppVersion.Parse(text));
    }
}
