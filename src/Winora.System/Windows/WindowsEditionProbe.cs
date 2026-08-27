using System.Globalization;
using System.Management;
using Microsoft.Win32;

namespace Winora.System.Windows;

/// <param name="Family">"Windows 11" or "Windows 10", worked out from the build number.</param>
/// <param name="Edition">Pro, Home, Education and so on, or empty when it cannot be read.</param>
/// <param name="DisplayVersion">The feature update, such as 25H2. Empty when absent.</param>
/// <param name="UpdateBuildRevision">The number after the build, or zero.</param>
/// <param name="InstalledUtc">When Windows was installed, or null when it cannot be read.</param>
/// <param name="TotalMemoryBytes">Installed memory, or zero when it cannot be read.</param>
/// <param name="MachineName">What this computer is called on the network.</param>
public sealed record WindowsEdition(
    string Family,
    string Edition,
    string DisplayVersion,
    int UpdateBuildRevision,
    DateTimeOffset? InstalledUtc,
    long TotalMemoryBytes,
    string MachineName);

/// <summary>Reads what edition of Windows this is, and when it arrived. Never changes anything.</summary>
public interface IWindowsEditionProbe
{
    WindowsEdition Read();
}

/// <summary>
/// The edition, the feature update and the installation date.
/// </summary>
/// <remarks>
/// <para>
/// The family name is worked out from the build number and never taken from
/// <c>ProductName</c>. Measured on this machine on 2026-08-27: <c>ProductName</c> reads
/// "Windows 10 Pro" on build 26200, which is Windows 11. Microsoft left the value at its Windows 10
/// text and it has stayed wrong for every Windows 11 build since. Printing it as it stands would be
/// telling somebody their computer runs an operating system it does not.
/// </para>
/// <para>
/// Only the edition word is taken from that value — "Pro" in the example above — because that part
/// is right and there is nowhere better to get it in a form a person recognises.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/sysinfo/registry-key-security-and-access-rights
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-computersystem
/// </remarks>
public sealed class WindowsEditionProbe : IWindowsEditionProbe
{
    private const string VersionKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>The first Windows 11 build. Anything at or above it is Windows 11.</summary>
    private const int FirstWindows11Build = 22000;

    private readonly IWindowsBuildProbe _build;

    public WindowsEditionProbe()
        : this(new WindowsBuildProbe())
    {
    }

    public WindowsEditionProbe(IWindowsBuildProbe build)
    {
        _build = build ?? throw new ArgumentNullException(nameof(build));
    }

    public WindowsEdition Read()
    {
        var build = _build.Read();

        using var key = OpenVersionKey();

        return new WindowsEdition(
            FamilyFor(build.Build),
            EditionOf(key?.GetValue("ProductName") as string),
            key?.GetValue("DisplayVersion") as string ?? string.Empty,
            key?.GetValue("UBR") is int revision ? revision : 0,
            InstalledAt(key),
            TotalMemory(),
            Environment.MachineName);
    }

    /// <summary>
    /// Which Windows this is, decided by the build number alone.
    /// </summary>
    /// <remarks>
    /// See the class remarks: the name Windows publishes for itself has been wrong since 2021.
    /// </remarks>
    public static string FamilyFor(int build) =>
        build >= FirstWindows11Build ? "Windows 11" : "Windows 10";

    /// <summary>
    /// The edition word out of a product name such as "Windows 10 Pro".
    /// </summary>
    /// <remarks>
    /// Everything up to and including the version number is dropped, because that part is the lie.
    /// A name in an unexpected shape comes back whole rather than mangled — an unfamiliar edition is
    /// worth showing as it stands.
    /// </remarks>
    public static string EditionOf(string? productName)
    {
        var trimmed = (productName ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 3 &&
            string.Equals(parts[0], "Windows", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                ? string.Join(' ', parts[2..])
                : trimmed;
    }

    private static RegistryKey? OpenVersionKey()
    {
        try
        {
            return Registry.LocalMachine.OpenSubKey(VersionKeyPath, writable: false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// When Windows was installed, from the seconds since 1970 it records.
    /// </summary>
    /// <remarks>
    /// Read as an unsigned value: it is stored as a DWORD, and a date past 2038 would come back
    /// negative if it were read as a signed one.
    /// </remarks>
    private static DateTimeOffset? InstalledAt(RegistryKey? key)
    {
        try
        {
            return key?.GetValue("InstallDate") is int seconds
                ? DateTimeOffset.FromUnixTimeSeconds(unchecked((uint)seconds))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long TotalMemory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");

            foreach (var item in searcher.Get().Cast<ManagementBaseObject>())
            {
                using (item)
                {
                    if (item["TotalPhysicalMemory"] is ulong total)
                    {
                        return (long)total;
                    }
                }
            }
        }
        catch (Exception)
        {
            // A machine that will not answer is reported as not answering, further up.
        }

        return 0;
    }
}
