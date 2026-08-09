using System.Management;

namespace Winora.System.Windows;

/// <param name="Name">Model as the processor reports it.</param>
/// <param name="BaseClockGigahertz">Rated base speed.</param>
/// <param name="Sockets">Physical processor packages.</param>
/// <param name="Cores">Physical cores.</param>
/// <param name="LogicalProcessors">Logical processors, counting simultaneous threading.</param>
/// <param name="IsVirtualizationEnabled">Whether firmware virtualization is turned on.</param>
/// <param name="L1CacheKilobytes">First-level cache, or zero when the firmware does not report it.</param>
/// <param name="L2CacheKilobytes">Second-level cache.</param>
/// <param name="L3CacheKilobytes">Third-level cache.</param>
public sealed record ProcessorFacts(
    string Name,
    double BaseClockGigahertz,
    int Sockets,
    int Cores,
    int LogicalProcessors,
    bool IsVirtualizationEnabled,
    int L1CacheKilobytes,
    int L2CacheKilobytes,
    int L3CacheKilobytes);

/// <summary>Reads the processor's fixed characteristics. Never changes anything.</summary>
public interface IProcessorFactsProbe
{
    ProcessorFacts Read();
}

/// <summary>
/// The unchanging half of the processor panel.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the live sampler because none of it moves: base speed, socket and core counts and
/// cache sizes are the same from boot to shutdown, and a WMI query costs tens of milliseconds. Read
/// once and kept.
/// </para>
/// <para>
/// L1 is not exposed by <c>Win32_Processor</c> at all, so it comes from <c>Win32_CacheMemory</c>,
/// which enumerates cache levels separately. Anything the firmware declines to report stays zero and
/// the screen omits that line rather than showing a confident nought.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-processor
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-cachememory
/// </remarks>
public sealed class WmiProcessorFactsProbe : IProcessorFactsProbe
{
    public ProcessorFacts Read()
    {
        var name = string.Empty;
        double baseClock = 0;
        var cores = 0;
        var logical = 0;
        var virtualization = false;
        var l2 = 0;
        var l3 = 0;
        var sockets = 0;

        foreach (var item in Query("Win32_Processor"))
        {
            sockets++;

            if (name.Length == 0)
            {
                name = Text(item, "Name");
            }

            if (baseClock == 0 && double.TryParse(Text(item, "MaxClockSpeed"), out var megahertz))
            {
                baseClock = megahertz / 1000d;
            }

            cores += Number(item, "NumberOfCores");
            logical += Number(item, "NumberOfLogicalProcessors");
            l2 += Number(item, "L2CacheSize");
            l3 += Number(item, "L3CacheSize");

            virtualization |= IsTrue(item, "VirtualizationFirmwareEnabled");
        }

        // Measured against Task Manager on a machine running Hyper-V, where the two disagreed:
        // VirtualizationFirmwareEnabled came back False while Task Manager said enabled. Once a
        // hypervisor is running, Windows is itself a guest and can no longer see the firmware flag —
        // but a hypervisor running is proof that virtualization is on. Either answer counts.
        virtualization |= Query("Win32_ComputerSystem").Any(item => IsTrue(item, "HypervisorPresent"));

        return new ProcessorFacts(
            name,
            baseClock,
            Math.Max(sockets, 1),
            cores,
            // Falls back to the runtime's count, which is always available even when WMI is not.
            logical > 0 ? logical : Environment.ProcessorCount,
            virtualization,
            LevelOneCacheKilobytes(),
            l2,
            l3);
    }

    /// <remarks>
    /// Level 3 in the CIM model is the first level of cache, not the third — the enumeration counts
    /// from "other" and "unknown". Getting that backwards would report the L3 size as L1.
    /// </remarks>
    private static int LevelOneCacheKilobytes()
    {
        var total = 0;

        foreach (var item in Query("Win32_CacheMemory"))
        {
            if (Number(item, "Level") == 3)
            {
                total += Number(item, "InstalledSize");
            }
        }

        return total;
    }

    private static IEnumerable<ManagementBaseObject> Query(string className)
    {
        ManagementObjectCollection results;
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT * FROM {className}");
            results = searcher.Get();
        }
        catch (Exception)
        {
            // A WMI repository that will not answer costs the panel a few lines, not the screen.
            yield break;
        }

        foreach (var item in results)
        {
            yield return item;
        }
    }

    private static bool IsTrue(ManagementBaseObject item, string property) =>
        string.Equals(Text(item, property), "True", StringComparison.OrdinalIgnoreCase);

    private static int Number(ManagementBaseObject item, string property) =>
        int.TryParse(Text(item, property), out var value) ? value : 0;

    private static string Text(ManagementBaseObject item, string property)
    {
        try
        {
            return item[property]?.ToString()?.Trim() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
