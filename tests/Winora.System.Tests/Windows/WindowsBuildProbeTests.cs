using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

public sealed class WindowsBuildFactsTests
{
    [Theory]
    [InlineData(10, 0, 26100, 10, 0, 22000, true)]
    [InlineData(10, 0, 22000, 10, 0, 22000, true)]
    [InlineData(10, 0, 19045, 10, 0, 22000, false)]
    [InlineData(10, 1, 1, 10, 0, 22000, true)]
    [InlineData(9, 9, 99999, 10, 0, 22000, false)]
    public void Minimum_comparison_orders_major_then_minor_then_build(
        int major,
        int minor,
        int build,
        int minimumMajor,
        int minimumMinor,
        int minimumBuild,
        bool expected)
    {
        var facts = new WindowsBuildFacts(major, minor, build);

        Assert.Equal(expected, facts.MeetsMinimum(minimumMajor, minimumMinor, minimumBuild));
    }

    [Fact]
    public void The_windows_11_baseline_is_build_22000()
    {
        Assert.Equal(10, WindowsBuildFacts.Windows11Baseline.Major);
        Assert.Equal(0, WindowsBuildFacts.Windows11Baseline.Minor);
        Assert.Equal(22000, WindowsBuildFacts.Windows11Baseline.Build);
    }

    [Fact]
    public void Negative_components_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsBuildFacts(10, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsBuildFacts(-1, 0, 22000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsBuildFacts(10, -1, 22000));
    }
}

public sealed class WindowsBuildProbeTests
{
    [Fact]
    public void The_probe_reports_the_running_operating_system_build()
    {
        var facts = new WindowsBuildProbe().Read();

        Assert.Equal(Environment.OSVersion.Version.Major, facts.Major);
        Assert.Equal(Environment.OSVersion.Version.Minor, facts.Minor);
        Assert.Equal(Environment.OSVersion.Version.Build, facts.Build);
    }

    [Fact]
    public void The_probe_is_read_only_and_repeatable()
    {
        var probe = new WindowsBuildProbe();

        Assert.Equal(probe.Read(), probe.Read());
    }

    [Fact]
    public void The_test_host_satisfies_the_supported_windows_11_baseline()
    {
        var facts = new WindowsBuildProbe().Read();

        Assert.True(
            facts.MeetsMinimum(
                WindowsBuildFacts.Windows11Baseline.Major,
                WindowsBuildFacts.Windows11Baseline.Minor,
                WindowsBuildFacts.Windows11Baseline.Build),
            $"Winora targets Windows 11 build 22000 or newer; the host reports {facts.Major}.{facts.Minor}.{facts.Build}.");
    }
}
