using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Winora.System.Windows;

/// <param name="Name">What the meter measures.</param>
/// <param name="Percent">Load from 0 to 100, or null when the meter has no percentage.</param>
/// <param name="Detail">The reading in its own units.</param>
public sealed record LiveMetric(string Name, double? Percent, string Detail);

/// <param name="ProcessorPercent">Total processor load.</param>
/// <param name="MemoryPercent">Physical memory in use.</param>
/// <param name="MemoryUsedGigabytes">Physical memory in use.</param>
/// <param name="MemoryTotalGigabytes">Physical memory installed.</param>
/// <param name="ProcessCount">Running processes.</param>
/// <param name="ThreadCount">Threads across those processes.</param>
/// <param name="HandleCount">Kernel handles across those processes.</param>
/// <param name="Uptime">Time since the machine started.</param>
/// <param name="Disks">Per-drive space.</param>
/// <param name="Networks">Per-adapter throughput.</param>
public sealed record LiveMetrics(
    double ProcessorPercent,
    double MemoryPercent,
    double MemoryUsedGigabytes,
    double MemoryTotalGigabytes,
    int ProcessCount,
    int ThreadCount,
    int HandleCount,
    TimeSpan Uptime,
    IReadOnlyList<LiveMetric> Disks,
    IReadOnlyList<LiveMetric> Networks);

/// <summary>Samples what the machine is doing right now. Never changes anything.</summary>
public interface ILiveMetricsProbe
{
    LiveMetrics Sample();
}

/// <summary>
/// The live figures behind the performance screen.
/// </summary>
/// <remarks>
/// <para>
/// Processor load is a rate, not a reading: <c>GetSystemTimes</c> returns totals accumulated since
/// boot, and the load is the change between two samples. The first sample therefore has nothing to
/// compare against and reports zero — the previous totals are kept so every later sample is real.
/// Computing it from a single reading would give the average since boot, which barely moves and
/// looks like a broken meter.
/// </para>
/// <para>
/// This is preferred over a performance counter deliberately: it is a documented Win32 call with no
/// extra dependency, and it cannot be disabled or corrupted the way the counter registry can.
/// Network throughput is derived the same way, from adapter byte totals and elapsed time.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes
/// </remarks>
public sealed partial class LiveMetricsProbe : ILiveMetricsProbe, IDisposable
{
    private const double BytesPerGigabyte = 1024d * 1024d * 1024d;
    private const double BytesPerMegabit = 1024d * 1024d / 8d;

    private readonly Lock _gate = new();
    private readonly ISystemLoadProbe _load;

    private (ulong Idle, ulong Kernel, ulong User)? _previousTimes;

    private readonly Dictionary<string, (long Bytes, DateTimeOffset At)> _networkMarks = [];

    public LiveMetricsProbe()
        : this(new WindowsSystemLoadProbe())
    {
    }

    public LiveMetricsProbe(ISystemLoadProbe load)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
    }

    public LiveMetrics Sample()
    {
        var load = _load.Read();
        var (processes, threads, handles) = ProcessTotals();

        return new LiveMetrics(
            ProcessorPercent(),
            load.MemoryLoadPercent,
            (load.TotalPhysicalBytes - load.AvailablePhysicalBytes) / BytesPerGigabyte,
            load.TotalPhysicalBytes / BytesPerGigabyte,
            processes,
            threads,
            handles,
            load.Uptime,
            Disks(),
            Networks());
    }

    private double ProcessorPercent()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return 0;
        }

        var idle = ToTicks(idleTime);
        var kernel = ToTicks(kernelTime);
        var user = ToTicks(userTime);

        lock (_gate)
        {
            var previous = _previousTimes;
            _previousTimes = (idle, kernel, user);

            if (previous is not { } was)
            {
                // Nothing to compare against yet. Zero here is honest for one sample; inventing a
                // figure from totals accumulated since boot would not be.
                return 0;
            }

            // Kernel time already includes idle, so the busy share is everything but the idle delta.
            var total = (kernel - was.Kernel) + (user - was.User);
            if (total == 0)
            {
                return 0;
            }

            var busy = total - (idle - was.Idle);
            return Math.Clamp(busy * 100d / total, 0, 100);
        }
    }

    private static ulong ToTicks(FileTime time) => ((ulong)time.High << 32) | time.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    /// <summary>
    /// Processes, threads and handles from one walk of the process list.
    /// </summary>
    /// <remarks>
    /// One pass rather than three: enumerating every process is the expensive part of a sample, and
    /// doing it repeatedly also risks the three totals describing slightly different moments.
    /// </remarks>
    private static (int Processes, int Threads, int Handles) ProcessTotals()
    {
        try
        {
            var processes = 0;
            var threads = 0;
            var handles = 0;

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    processes++;

                    try
                    {
                        threads += process.Threads.Count;
                        handles += process.HandleCount;
                    }
                    catch (Exception)
                    {
                        // A process that exited between the listing and the read, or one this token
                        // cannot open. Counted as running, but its details are not invented.
                    }
                }
            }

            return (processes, threads, handles);
        }
        catch (Exception)
        {
            return (0, 0, 0);
        }
    }

    /// <summary>
    /// True for something a person would call a network adapter.
    /// </summary>
    /// <remarks>
    /// <c>GetAllNetworkInterfaces</c> returns every NDIS binding, not just the cards: each real
    /// adapter also appears once per attached filter driver — WFP layers, the QoS packet scheduler,
    /// virtual switch extensions — plus tunnel pseudo-interfaces. On one ordinary desktop that is
    /// thirty-odd entries for two adapters, which is what the screen showed before this existed.
    /// Task Manager lists the cards, and so does this.
    /// </remarks>
    private static bool IsRealAdapter(NetworkInterface adapter)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up ||
            adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        // Filter instances are named after the adapter they attach to with a numeric suffix, or
        // after the filter itself.
        if (adapter.Name.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        string[] markers = ["Filter", "Scheduler", "Pseudo-Interface", "Extension"];
        return !markers.Any(marker =>
            adapter.Name.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
            adapter.Description.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<LiveMetric> Disks()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(static drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
                .Select(static drive =>
                {
                    var used = drive.TotalSize - drive.TotalFreeSpace;
                    var percent = drive.TotalSize > 0 ? used * 100d / drive.TotalSize : 0;

                    return new LiveMetric(
                        drive.Name.TrimEnd('\\'),
                        percent,
                        $"{used / BytesPerGigabyte:F0} из {drive.TotalSize / BytesPerGigabyte:F0} ГБ занято");
                })
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private IReadOnlyList<LiveMetric> Networks()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var metrics = new List<LiveMetric>();

            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsRealAdapter(adapter))
                {
                    continue;
                }

                var statistics = adapter.GetIPStatistics();
                var total = statistics.BytesSent + statistics.BytesReceived;

                double megabits = 0;
                lock (_gate)
                {
                    if (_networkMarks.TryGetValue(adapter.Id, out var previous))
                    {
                        var seconds = (now - previous.At).TotalSeconds;
                        if (seconds > 0.05)
                        {
                            megabits = Math.Max(0, (total - previous.Bytes) / BytesPerMegabit / seconds);
                        }
                    }

                    _networkMarks[adapter.Id] = (total, now);
                }

                metrics.Add(new LiveMetric(
                    adapter.Name,
                    Percent: null,
                    $"{megabits:F1} Мбит/с"));
            }

            return metrics;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <remarks>
    /// Nothing unmanaged is held any more — the processor reading moved from a performance counter
    /// to <c>GetSystemTimes</c>. The interface stays so callers registered as disposable keep
    /// working, and so adding a handle here later does not become a leak nobody notices.
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            _previousTimes = null;
            _networkMarks.Clear();
        }
    }
}
