using Winora.System.Windows;

namespace Winora.App.Services;

/// <param name="Label">What the figure is.</param>
/// <param name="Value">The figure, already formatted.</param>
public sealed record StatView(string Label, string Value);

/// <param name="Key">Stable identity, so a panel is updated rather than rebuilt each second.</param>
/// <param name="TitleKey">Resource key for the kind, or empty when the name speaks for itself.</param>
/// <param name="Name">What this panel measures.</param>
/// <param name="Subtitle">The hardware behind it, or empty.</param>
/// <param name="Percent">Load from 0 to 100, or null when the panel has no percentage.</param>
/// <param name="Reading">The headline figure in its own units.</param>
/// <param name="Stats">The detail rows shown when the panel is selected.</param>
public sealed record PerformancePanelView(
    string Key,
    string TitleKey,
    string Name,
    string Subtitle,
    double? Percent,
    string Reading,
    IReadOnlyList<StatView> Stats);

/// <summary>Everything the performance screen shows.</summary>
public interface IPerformanceService
{
    /// <summary>One panel per component, in the order the screen lists them.</summary>
    IReadOnlyList<PerformancePanelView> Sample();
}

/// <inheritdoc />
public sealed class PerformanceService : IPerformanceService
{
    private readonly ILiveMetricsProbe _live;
    private readonly IHardwareInventoryProbe _hardware;
    private readonly IProcessorFactsProbe _processorFacts;

    private ProcessorFacts? _processor;
    private HardwareInventory? _inventory;

    public PerformanceService(
        ILiveMetricsProbe live,
        IHardwareInventoryProbe hardware,
        IProcessorFactsProbe processorFacts)
    {
        _live = live ?? throw new ArgumentNullException(nameof(live));
        _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        _processorFacts = processorFacts ?? throw new ArgumentNullException(nameof(processorFacts));
    }

    public IReadOnlyList<PerformancePanelView> Sample()
    {
        // Read once and kept: none of it changes while the app is open, and a WMI query costs tens
        // of milliseconds — far too much to repeat every second.
        _processor ??= _processorFacts.Read();
        _inventory ??= _hardware.Read();

        var metrics = _live.Sample();
        var panels = new List<PerformancePanelView>
        {
            Processor(metrics, _processor),
            Memory(metrics, _inventory),
        };

        panels.AddRange(metrics.Disks.Select((disk, index) => Disk(disk, index, _inventory)));
        panels.AddRange(metrics.Networks.Select(Network));
        panels.AddRange(_inventory.GraphicsAdapters.Select(Graphics));

        return panels;
    }

    private static PerformancePanelView Processor(LiveMetrics metrics, ProcessorFacts facts)
    {
        var stats = new List<StatView>
        {
            new("Performance_Stat_Utilisation", $"{metrics.ProcessorPercent:F0}%"),
            new("Performance_Stat_Processes", metrics.ProcessCount.ToString("N0")),
            new("Performance_Stat_Threads", metrics.ThreadCount.ToString("N0")),
            new("Performance_Stat_Handles", metrics.HandleCount.ToString("N0")),
            new("Performance_Stat_Uptime", Uptime(metrics.Uptime)),
        };

        // Each of these is omitted rather than shown as zero when the firmware does not report it.
        // A confident nought reads as a measurement; a missing row reads as missing.
        if (facts.BaseClockGigahertz > 0)
        {
            stats.Insert(1, new StatView("Performance_Stat_BaseSpeed", $"{facts.BaseClockGigahertz:F2} ГГц"));
        }

        stats.Add(new StatView("Performance_Stat_Sockets", facts.Sockets.ToString("N0")));

        if (facts.Cores > 0)
        {
            stats.Add(new StatView("Performance_Stat_Cores", facts.Cores.ToString("N0")));
        }

        stats.Add(new StatView("Performance_Stat_Logical", facts.LogicalProcessors.ToString("N0")));
        stats.Add(new StatView(
            "Performance_Stat_Virtualisation",
            facts.IsVirtualizationEnabled ? "Performance_Value_On" : "Performance_Value_Off"));

        AddCache(stats, "Performance_Stat_L1", facts.L1CacheKilobytes);
        AddCache(stats, "Performance_Stat_L2", facts.L2CacheKilobytes);
        AddCache(stats, "Performance_Stat_L3", facts.L3CacheKilobytes);

        return new PerformancePanelView(
            "cpu",
            "Performance_Panel_Processor",
            "Performance_Panel_Processor",
            facts.Name,
            metrics.ProcessorPercent,
            $"{metrics.ProcessorPercent:F0}%",
            stats);
    }

    private static PerformancePanelView Memory(LiveMetrics metrics, HardwareInventory inventory)
    {
        var stats = new List<StatView>
        {
            new("Performance_Stat_InUse", $"{metrics.MemoryUsedGigabytes:F1} ГБ"),
            new("Performance_Stat_Available", $"{metrics.MemoryTotalGigabytes - metrics.MemoryUsedGigabytes:F1} ГБ"),
            new("Performance_Stat_Total", $"{metrics.MemoryTotalGigabytes:F1} ГБ"),
            new("Performance_Stat_Slots", inventory.Memory.Count.ToString("N0")),
        };

        // The first module's figures stand for the set: mixing speeds is unusual, and reporting one
        // module's speed as if it were the system's would be wrong if they did differ.
        if (inventory.Memory.FirstOrDefault() is { Detail.Length: > 0 } module)
        {
            stats.Add(new StatView("Performance_Stat_Module", module.Detail));
        }

        return new PerformancePanelView(
            "memory",
            "Performance_Panel_Memory",
            "Performance_Panel_Memory",
            $"{metrics.MemoryTotalGigabytes:F1} ГБ",
            metrics.MemoryPercent,
            $"{metrics.MemoryUsedGigabytes:F1} / {metrics.MemoryTotalGigabytes:F1} ГБ",
            stats);
    }

    private static PerformancePanelView Disk(LiveMetric disk, int index, HardwareInventory inventory)
    {
        var hardware = index < inventory.Disks.Count ? inventory.Disks[index] : null;

        var stats = new List<StatView> { new("Performance_Stat_Space", disk.Detail) };
        if (hardware is not null)
        {
            stats.Add(new StatView("Performance_Stat_Model", hardware.Name));
            if (hardware.Detail.Length > 0)
            {
                stats.Add(new StatView("Performance_Stat_Capacity", hardware.Detail));
            }
        }

        return new PerformancePanelView(
            "disk:" + disk.Name,
            "Performance_Panel_Disk",
            disk.Name,
            hardware?.Name ?? string.Empty,
            disk.Percent,
            $"{disk.Percent:F0}%",
            stats);
    }

    private static PerformancePanelView Network(LiveMetric adapter) =>
        new(
            "net:" + adapter.Name,
            "Performance_Panel_Network",
            adapter.Name,
            string.Empty,
            // Throughput has no ceiling to be a percentage of, so this panel shows a figure only.
            null,
            adapter.Detail,
            [new StatView("Performance_Stat_Throughput", adapter.Detail)]);

    private static PerformancePanelView Graphics(HardwareComponent adapter)
    {
        var stats = new List<StatView> { new("Performance_Stat_Model", adapter.Name) };
        if (adapter.Detail.Length > 0)
        {
            stats.Add(new StatView("Performance_Stat_Capacity", adapter.Detail));
        }

        return new PerformancePanelView(
            "gpu:" + adapter.Name,
            "Performance_Panel_Graphics",
            adapter.Name,
            adapter.Detail,
            // Deliberately absent. Reading GPU load needs performance counters whose instance names
            // differ per vendor and build, and a made-up figure on this screen would be worse than
            // none — the panel lists what the adapter is instead of pretending to measure it.
            null,
            string.Empty,
            stats);
    }

    private static void AddCache(List<StatView> stats, string labelKey, int kilobytes)
    {
        if (kilobytes <= 0)
        {
            return;
        }

        stats.Add(new StatView(
            labelKey,
            kilobytes >= 1024 ? $"{kilobytes / 1024d:F1} МБ" : $"{kilobytes} КБ"));
    }

    private static string Uptime(TimeSpan uptime) =>
        $"{(int)uptime.TotalDays}:{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";
}
