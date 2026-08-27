using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Windows;

/// <summary>
/// Which Windows this is, and why it is not asked of Windows directly.
/// </summary>
/// <remarks>
/// <c>ProductName</c> in the registry reads "Windows 10 Pro" on build 26200, which is Windows 11.
/// Measured on the owner's machine on 2026-08-27. Microsoft left the value at its Windows 10 text
/// and it has been wrong for every Windows 11 build since 2021, so the family name is worked out
/// from the build number and only the edition word is taken from that string.
/// </remarks>
public sealed class WindowsEditionProbeTests
{
    private sealed class Build(int build) : IWindowsBuildProbe
    {
        public WindowsBuildFacts Read() => new(10, 0, build);
    }

    [Theory]
    [InlineData(26200, "Windows 11")]
    [InlineData(22000, "Windows 11")]
    [InlineData(21999, "Windows 10")]
    [InlineData(19045, "Windows 10")]
    public void The_family_comes_from_the_build_number(int build, string expected)
    {
        Assert.Equal(expected, WindowsEditionProbe.FamilyFor(build));
    }

    /// <summary>
    /// The exact string this machine reports, and the exact answer it must not produce.
    /// </summary>
    [Fact]
    public void The_name_Windows_publishes_for_itself_is_not_believed()
    {
        Assert.Equal("Pro", WindowsEditionProbe.EditionOf("Windows 10 Pro"));
        Assert.Equal("Windows 11", WindowsEditionProbe.FamilyFor(26200));
    }

    [Theory]
    [InlineData("Windows 11 Home Single Language", "Home Single Language")]
    [InlineData("Windows 10 Education", "Education")]
    [InlineData("Windows 10 Pro for Workstations", "Pro for Workstations")]
    public void The_edition_word_is_taken_from_the_product_name(string product, string expected)
    {
        Assert.Equal(expected, WindowsEditionProbe.EditionOf(product));
    }

    /// <summary>
    /// A name in an unexpected shape is shown whole rather than cut at a guess.
    /// </summary>
    /// <remarks>
    /// Server editions and anything Microsoft names differently in future land here. Half of an
    /// unfamiliar name is worse than all of it.
    /// </remarks>
    [Theory]
    [InlineData("Windows Server 2022 Datacenter")]
    [InlineData("Something Else Entirely")]
    public void An_unfamiliar_product_name_comes_back_whole(string product)
    {
        Assert.Equal(product, WindowsEditionProbe.EditionOf(product));
    }

    [Fact]
    public void Nothing_at_all_is_answered_with_nothing()
    {
        Assert.Equal(string.Empty, WindowsEditionProbe.EditionOf(null));
        Assert.Equal(string.Empty, WindowsEditionProbe.EditionOf("   "));
    }

    /// <summary>
    /// The probe answers on this machine, and answers consistently with the build it was given.
    /// </summary>
    /// <remarks>
    /// Reads the real registry, which is the point: everything else here is arithmetic on strings,
    /// and this is the one check that the values are where the code believes they are.
    /// </remarks>
    [Fact]
    public void The_probe_reads_this_machine()
    {
        var edition = new WindowsEditionProbe(new Build(26200)).Read();

        Assert.Equal("Windows 11", edition.Family);
        Assert.False(string.IsNullOrWhiteSpace(edition.MachineName));
        Assert.True(edition.TotalMemoryBytes > 0);
        Assert.NotNull(edition.InstalledUtc);
        Assert.True(edition.InstalledUtc > new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }
}
