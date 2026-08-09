using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// Read-only checks against the real machine. Nothing here switches the active plan: these tests
/// run on the developer's own desktop and a test that changes power behaviour would be a test that
/// changes how the machine runs.
/// </summary>
public sealed class PowerSchemeAccessTests
{
    [Fact]
    public void Windows_reports_at_least_one_power_plan()
    {
        var schemes = new WindowsPowerSchemeAccess().Schemes();

        Assert.NotEmpty(schemes);
    }

    [Fact]
    public void Every_plan_has_an_identity_and_a_readable_name()
    {
        foreach (var scheme in new WindowsPowerSchemeAccess().Schemes())
        {
            Assert.NotEqual(Guid.Empty, scheme.Id);
            Assert.False(string.IsNullOrWhiteSpace(scheme.Name));
        }
    }

    /// <summary>
    /// Exactly one plan is in force. Reporting none would leave the screen unable to show what is
    /// selected; reporting several would mean the active-plan read is wrong.
    /// </summary>
    [Fact]
    public void Exactly_one_plan_is_active()
    {
        var schemes = new WindowsPowerSchemeAccess().Schemes();

        Assert.Equal(1, schemes.Count(static scheme => scheme.IsActive));
    }

    [Fact]
    public void Plans_are_distinct()
    {
        var ids = new WindowsPowerSchemeAccess().Schemes().Select(static scheme => scheme.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    /// <summary>A plan this machine does not have must be refused, not silently accepted.</summary>
    [Fact]
    public void Activating_an_unknown_plan_fails()
    {
        Assert.False(new WindowsPowerSchemeAccess().Activate(Guid.NewGuid()));
    }
}

/// <summary>The performance screen shows measurements, so the measurements have to be sane.</summary>
public sealed class SystemLoadProbeTests
{
    [Fact]
    public void The_reading_describes_a_real_machine()
    {
        var load = new WindowsSystemLoadProbe().Read();

        Assert.InRange(load.MemoryLoadPercent, 1, 100);
        Assert.True(load.TotalPhysicalBytes > 0);
        Assert.True(load.AvailablePhysicalBytes <= load.TotalPhysicalBytes);
        Assert.True(load.LogicalProcessors >= 1);
        Assert.True(load.Uptime > TimeSpan.Zero);
    }

    [Fact]
    public void Reading_twice_does_not_change_anything()
    {
        var probe = new WindowsSystemLoadProbe();

        var first = probe.Read();
        var second = probe.Read();

        // Free memory moves constantly; installed memory and processor count do not.
        Assert.Equal(first.TotalPhysicalBytes, second.TotalPhysicalBytes);
        Assert.Equal(first.LogicalProcessors, second.LogicalProcessors);
    }
}
