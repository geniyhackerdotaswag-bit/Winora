using System.Globalization;
using Winora.System.Windows;

namespace Winora.App.Services;

/// <param name="LabelKey">Resource key for what the figure is.</param>
/// <param name="Value">The figure, already formatted and ready to show.</param>
public sealed record MachineFact(string LabelKey, string Value);

/// <param name="TitleKey">Resource key for the group heading.</param>
/// <param name="Facts">The rows under it. Never empty — a group with nothing to say is dropped.</param>
public sealed record MachineGroup(string TitleKey, IReadOnlyList<MachineFact> Facts);

/// <summary>Everything the "My computer" screen shows. Reads only; changes nothing.</summary>
public interface IMachineSummaryService
{
    IReadOnlyList<MachineGroup> Read();
}

/// <summary>
/// The machine's description, gathered from the read-only probes.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here can change anything, so none of it goes through the change pipeline: there is no
/// plan, no backup and no undo, because there is nothing to undo. That is the whole character of
/// this screen — it answers "what is this computer" and does not offer to alter the answer.
/// </para>
/// <para>
/// A value that cannot be read is left out rather than shown as a zero or a dash. A blank line and
/// a confident nought look the same at a glance and mean opposite things.
/// </para>
/// </remarks>
public sealed class MachineSummaryService : IMachineSummaryService
{
    private const double BytesPerGigabyte = 1024d * 1024d * 1024d;

    private readonly IWindowsEditionProbe _edition;
    private readonly IWindowsBuildProbe _build;
    private readonly IHardwareInventoryProbe _hardware;
    private readonly IProcessorFactsProbe _processor;

    public MachineSummaryService(
        IWindowsEditionProbe edition,
        IWindowsBuildProbe build,
        IHardwareInventoryProbe hardware,
        IProcessorFactsProbe processor)
    {
        _edition = edition ?? throw new ArgumentNullException(nameof(edition));
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    public IReadOnlyList<MachineGroup> Read()
    {
        var groups = new List<MachineGroup>
        {
            Group("Machine_Group_Windows", Windows()),
            Group("Machine_Group_Processor", Processor()),
            Group("Machine_Group_Memory", Memory()),
            Group("Machine_Group_Graphics", Graphics()),
            Group("Machine_Group_Disks", Disks()),
            Group("Machine_Group_Board", Board()),
        };

        // A group with nothing readable under it is dropped rather than shown empty. A virtual
        // machine has no motherboard to report, and a heading over a blank space reads as a fault.
        return [.. groups.Where(static group => group.Facts.Count > 0)];
    }

    private static MachineGroup Group(string titleKey, IEnumerable<MachineFact> facts) =>
        new(titleKey, [.. facts.Where(static fact => !string.IsNullOrWhiteSpace(fact.Value))]);

    private IEnumerable<MachineFact> Windows()
    {
        var edition = _edition.Read();
        var build = _build.Read();

        var name = string.IsNullOrWhiteSpace(edition.Edition)
            ? edition.Family
            : $"{edition.Family} {edition.Edition}";

        // Издание и выпуск одной строкой: «Windows 10 Pro, 22H2».
        //
        // Двумя строками это читалось как два разных факта, хотя человек называет их вместе, когда
        // его спрашивают, какая у него Windows. Объединено по просьбе владельца 3 сентября 2026.
        //
        // Выпуск приклеивается, только если он прочитался: на сборках, где `DisplayVersion` пуст,
        // строка иначе оканчивалась бы висящей запятой.
        yield return new MachineFact(
            "Machine_Windows_Version",
            string.IsNullOrWhiteSpace(edition.DisplayVersion)
                ? name
                : $"{name}, {edition.DisplayVersion}");

        yield return new MachineFact(
            "Machine_Windows_Build",
            edition.UpdateBuildRevision > 0
                ? $"{build.Build}.{edition.UpdateBuildRevision}"
                : build.Build.ToString(CultureInfo.CurrentCulture));

        yield return new MachineFact(
            "Machine_Windows_Installed",
            edition.InstalledUtc is { } installed
                ? installed.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)
                : string.Empty);

        yield return new MachineFact("Machine_Windows_Name", edition.MachineName);
    }

    private IEnumerable<MachineFact> Processor()
    {
        var facts = _processor.Read();

        yield return new MachineFact("Machine_Processor_Name", facts.Name);

        yield return new MachineFact(
            "Machine_Processor_Cores",
            facts.Cores > 0 && facts.LogicalProcessors > 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} / {1}",
                    facts.Cores,
                    facts.LogicalProcessors)
                : string.Empty);

        yield return new MachineFact(
            "Machine_Processor_Clock",
            facts.BaseClockGigahertz > 0
                ? facts.BaseClockGigahertz.ToString("0.00", CultureInfo.CurrentCulture) + " GHz"
                : string.Empty);

        yield return new MachineFact(
            "Machine_Processor_Cache",
            facts.L3CacheKilobytes > 0
                ? Megabytes(facts.L3CacheKilobytes * 1024d)
                : string.Empty);
    }

    private IEnumerable<MachineFact> Memory()
    {
        var total = _edition.Read().TotalMemoryBytes;

        yield return new MachineFact(
            "Machine_Memory_Total",
            total > 0 ? Gigabytes(total) : string.Empty);

        var modules = _hardware.Read().Memory;

        for (var index = 0; index < modules.Count; index++)
        {
            var module = modules[index];

            yield return new MachineFact(
                "Machine_Memory_Module",
                string.IsNullOrWhiteSpace(module.Detail) ? module.Name : $"{module.Name} — {module.Detail}");
        }
    }

    private IEnumerable<MachineFact> Graphics() =>
        _hardware.Read().GraphicsAdapters.Select(static adapter => new MachineFact(
            "Machine_Graphics_Adapter",
            string.IsNullOrWhiteSpace(adapter.Detail) ? adapter.Name : $"{adapter.Name} — {adapter.Detail}"));

    private IEnumerable<MachineFact> Disks() =>
        _hardware.Read().Disks.Select(static disk => new MachineFact(
            "Machine_Disk",
            string.IsNullOrWhiteSpace(disk.Detail) ? disk.Name : $"{disk.Name} — {disk.Detail}"));

    private IEnumerable<MachineFact> Board()
    {
        var board = _hardware.Read().Motherboard;

        if (board is not null)
        {
            yield return new MachineFact(
                "Machine_Board",
                string.IsNullOrWhiteSpace(board.Detail) ? board.Name : $"{board.Name} — {board.Detail}");
        }
    }

    private static string Gigabytes(double bytes) =>
        (bytes / BytesPerGigabyte).ToString("0.#", CultureInfo.CurrentCulture) + " ГБ";

    private static string Megabytes(double bytes) =>
        (bytes / (1024d * 1024d)).ToString("0.#", CultureInfo.CurrentCulture) + " МБ";
}
