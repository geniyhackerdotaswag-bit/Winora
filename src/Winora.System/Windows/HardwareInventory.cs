using System.Management;

namespace Winora.System.Windows;

/// <param name="Name">Model as the device reports it.</param>
/// <param name="Detail">One line of the figures worth knowing, or empty.</param>
public sealed record HardwareComponent(string Name, string Detail);

/// <param name="Processor">The CPU.</param>
/// <param name="Memory">Installed memory, one entry per module.</param>
/// <param name="GraphicsAdapters">Every display adapter Windows knows about.</param>
/// <param name="Disks">Physical drives.</param>
/// <param name="Motherboard">The board, or null when the firmware does not say.</param>
public sealed record HardwareInventory(
    HardwareComponent? Processor,
    IReadOnlyList<HardwareComponent> Memory,
    IReadOnlyList<HardwareComponent> GraphicsAdapters,
    IReadOnlyList<HardwareComponent> Disks,
    HardwareComponent? Motherboard);

/// <summary>Reads what hardware is in this machine. Never changes anything.</summary>
public interface IHardwareInventoryProbe
{
    HardwareInventory Read();
}

/// <summary>
/// The machine's components, through WMI.
/// </summary>
/// <remarks>
/// <para>
/// Read-only and slow enough to be worth caching by the caller — a WMI query costs tens of
/// milliseconds and the answer does not change while the app is open.
/// </para>
/// <para>
/// Every field is optional. A virtual machine reports no motherboard serial, some firmware leaves
/// memory speed blank, and a laptop with switchable graphics lists two adapters. Anything missing is
/// simply left out rather than filled with a guess or a zero that reads as a real measurement.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/computer-system-hardware-classes
/// </remarks>
public sealed class WmiHardwareInventoryProbe : IHardwareInventoryProbe
{
    private const double BytesPerGigabyte = 1024d * 1024d * 1024d;

    public HardwareInventory Read() =>
        new(
            Processor(),
            Query("Win32_PhysicalMemory", MemoryModule),
            Query("Win32_VideoController", GraphicsAdapter),
            Query("Win32_DiskDrive", Disk),
            Query("Win32_BaseBoard", Motherboard).FirstOrDefault());

    private static HardwareComponent? Processor() =>
        Query("Win32_Processor", static item =>
        {
            var cores = Text(item, "NumberOfCores");
            var threads = Text(item, "NumberOfLogicalProcessors");
            var clock = Text(item, "MaxClockSpeed");

            var parts = new List<string>();
            if (cores.Length > 0 && threads.Length > 0)
            {
                parts.Add($"{cores} ядер / {threads} потоков");
            }

            if (clock.Length > 0 && double.TryParse(clock, out var megahertz))
            {
                parts.Add($"{megahertz / 1000:F2} ГГц");
            }

            return new HardwareComponent(Text(item, "Name"), string.Join(" · ", parts));
        }).FirstOrDefault();

    private static HardwareComponent MemoryModule(ManagementBaseObject item)
    {
        var capacity = Text(item, "Capacity");
        var speed = Text(item, "ConfiguredClockSpeed") is { Length: > 0 } configured
            ? configured
            : Text(item, "Speed");

        var parts = new List<string>();
        if (ulong.TryParse(capacity, out var bytes) && bytes > 0)
        {
            parts.Add($"{bytes / BytesPerGigabyte:F0} ГБ");
        }

        if (speed.Length > 0)
        {
            parts.Add($"{speed} МГц");
        }

        var slot = Text(item, "DeviceLocator");
        var name = Text(item, "Manufacturer") is { Length: > 0 } maker && slot.Length > 0
            ? $"{maker} · {slot}"
            : slot.Length > 0 ? slot : "Модуль памяти";

        return new HardwareComponent(name, string.Join(" · ", parts));
    }

    private static HardwareComponent GraphicsAdapter(ManagementBaseObject item)
    {
        var parts = new List<string>();

        // AdapterRAM is a 32-bit field, so anything above four gigabytes wraps and reports nonsense.
        // A wrong number stated confidently is worse than none, so it is only used when plausible.
        if (uint.TryParse(Text(item, "AdapterRAM"), out var videoBytes) && videoBytes > 0)
        {
            parts.Add($"{videoBytes / BytesPerGigabyte:F1} ГБ");
        }

        if (Text(item, "DriverVersion") is { Length: > 0 } driver)
        {
            parts.Add("драйвер " + driver);
        }

        return new HardwareComponent(Text(item, "Name"), string.Join(" · ", parts));
    }

    private static HardwareComponent Disk(ManagementBaseObject item)
    {
        var parts = new List<string>();
        if (ulong.TryParse(Text(item, "Size"), out var bytes) && bytes > 0)
        {
            parts.Add($"{bytes / BytesPerGigabyte:F0} ГБ");
        }

        if (Text(item, "InterfaceType") is { Length: > 0 } bus)
        {
            parts.Add(bus);
        }

        return new HardwareComponent(Text(item, "Model"), string.Join(" · ", parts));
    }

    private static HardwareComponent Motherboard(ManagementBaseObject item) =>
        new(
            string.Join(" ", new[] { Text(item, "Manufacturer"), Text(item, "Product") }
                .Where(static part => part.Length > 0)),
            string.Empty);

    private static IReadOnlyList<HardwareComponent> Query(
        string className,
        Func<ManagementBaseObject, HardwareComponent> project)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT * FROM {className}");
            return searcher.Get()
                .Cast<ManagementBaseObject>()
                .Select(project)
                .Where(static component => component.Name.Length > 0)
                .ToArray();
        }
        catch (Exception)
        {
            // A class this build does not expose, or a WMI repository that will not answer. Neither
            // is worth failing the screen over: the component is simply not listed.
            return [];
        }
    }

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
