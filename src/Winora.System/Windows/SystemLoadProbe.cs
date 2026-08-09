using System.Runtime.InteropServices;

namespace Winora.System.Windows;

/// <param name="MemoryLoadPercent">How much physical memory is in use, 0 to 100.</param>
/// <param name="TotalPhysicalBytes">Installed physical memory.</param>
/// <param name="AvailablePhysicalBytes">Physical memory still free.</param>
/// <param name="LogicalProcessors">Logical processors visible to Windows.</param>
/// <param name="Uptime">How long since the machine last started.</param>
public sealed record SystemLoad(
    int MemoryLoadPercent,
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    int LogicalProcessors,
    TimeSpan Uptime);

/// <summary>Reads what the machine is actually doing. Never changes anything.</summary>
public interface ISystemLoadProbe
{
    SystemLoad Read();
}

/// <summary>
/// Live figures for the performance screen.
/// </summary>
/// <remarks>
/// Deliberately measurements rather than advice. A performance screen that offers to "optimise"
/// things it cannot measure is how these tools end up shipping folklore; this one shows what is
/// true and offers the one documented setting that genuinely trades power for speed.
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex
/// </remarks>
public sealed partial class WindowsSystemLoadProbe : ISystemLoadProbe
{
    public SystemLoad Read()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>(),
        };

        if (!GlobalMemoryStatusEx(ref status))
        {
            status = default;
        }

        return new SystemLoad(
            (int)status.MemoryLoad,
            status.TotalPhysical,
            status.AvailablePhysical,
            Environment.ProcessorCount,
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
