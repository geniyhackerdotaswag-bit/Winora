using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// Live readings against the real machine. Nothing here changes anything.
/// </summary>
public sealed class LiveMetricsProbeTests
{
    /// <summary>
    /// Processor load is a rate. A probe that computes it from a single reading returns the average
    /// since boot, which is a number that barely moves and looks plausible — the worst kind of wrong.
    /// </summary>
    [Fact]
    public void The_first_sample_reports_no_processor_load_and_the_second_is_real()
    {
        using var probe = new LiveMetricsProbe();

        Assert.Equal(0, probe.Sample().ProcessorPercent);

        // Long enough for the kernel to accumulate a measurable difference.
        Thread.Sleep(300);

        var second = probe.Sample();
        Assert.InRange(second.ProcessorPercent, 0, 100);
    }

    [Fact]
    public void Memory_matches_the_machine()
    {
        using var probe = new LiveMetricsProbe();
        var sample = probe.Sample();

        Assert.InRange(sample.MemoryPercent, 1, 100);
        Assert.True(sample.MemoryTotalGigabytes > 0);
        Assert.True(sample.MemoryUsedGigabytes > 0);
        Assert.True(sample.MemoryUsedGigabytes <= sample.MemoryTotalGigabytes);
    }

    [Fact]
    public void Processes_and_threads_are_counted()
    {
        using var probe = new LiveMetricsProbe();
        var sample = probe.Sample();

        // This test is itself a process with threads, so neither can legitimately be zero.
        Assert.True(sample.ProcessCount > 0);
        Assert.True(sample.ThreadCount >= sample.ProcessCount);
        Assert.True(sample.Uptime > TimeSpan.Zero);
    }

    /// <summary>Every machine has a system drive, so an empty disk list means the read failed.</summary>
    [Fact]
    public void Fixed_drives_are_listed_with_a_plausible_share_used()
    {
        using var probe = new LiveMetricsProbe();
        var sample = probe.Sample();

        Assert.NotEmpty(sample.Disks);

        foreach (var disk in sample.Disks)
        {
            Assert.False(string.IsNullOrWhiteSpace(disk.Name));
            Assert.NotNull(disk.Percent);
            Assert.InRange(disk.Percent!.Value, 0, 100);
        }
    }

    /// <summary>
    /// Throughput is also a rate, so the first sample cannot know one. What matters is that it never
    /// reports a negative or absurd figure once it does.
    /// </summary>
    [Fact]
    public void Network_throughput_is_never_negative()
    {
        using var probe = new LiveMetricsProbe();
        probe.Sample();
        Thread.Sleep(200);

        foreach (var adapter in probe.Sample().Networks)
        {
            Assert.False(string.IsNullOrWhiteSpace(adapter.Name));
            Assert.Null(adapter.Percent);
            Assert.DoesNotContain("-", adapter.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Sampling_repeatedly_does_not_drift_out_of_range()
    {
        using var probe = new LiveMetricsProbe();

        for (var index = 0; index < 5; index++)
        {
            var sample = probe.Sample();
            Assert.InRange(sample.ProcessorPercent, 0, 100);
            Assert.InRange(sample.MemoryPercent, 0, 100);
            Thread.Sleep(60);
        }
    }
}

/// <summary>The machine's components, read from WMI.</summary>
public sealed class HardwareInventoryProbeTests
{
    private static readonly HardwareInventory Inventory = new WmiHardwareInventoryProbe().Read();

    [Fact]
    public void The_processor_is_identified()
    {
        Assert.NotNull(Inventory.Processor);
        Assert.False(string.IsNullOrWhiteSpace(Inventory.Processor!.Name));
    }

    [Fact]
    public void At_least_one_memory_module_and_one_disk_are_listed()
    {
        Assert.NotEmpty(Inventory.Memory);
        Assert.NotEmpty(Inventory.Disks);
    }

    [Fact]
    public void At_least_one_display_adapter_is_listed()
    {
        Assert.NotEmpty(Inventory.GraphicsAdapters);
    }

    /// <summary>
    /// A component with no name would render as an empty card. The probe drops those rather than
    /// showing a blank row, so nothing that comes back may be nameless.
    /// </summary>
    [Fact]
    public void No_listed_component_is_nameless()
    {
        var all = Inventory.Memory
            .Concat(Inventory.GraphicsAdapters)
            .Concat(Inventory.Disks);

        foreach (var component in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(component.Name));
        }
    }
}
