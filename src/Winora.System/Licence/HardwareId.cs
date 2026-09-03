using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Winora.System.Licence;

/// <summary>What this computer is, as far as a licence is concerned.</summary>
public interface IHardwareId
{
    /// <summary>The fingerprint sent to the site. Empty when it cannot be worked out.</summary>
    string Value { get; }
}

/// <summary>
/// A stable fingerprint of this machine, hashed before it leaves.
/// </summary>
/// <remarks>
/// <para>
/// Two parts, and both are deliberate. <c>MachineGuid</c> is written by Windows at installation and
/// survives every hardware change; the system volume serial survives a Windows reinstall on the
/// same disk. Neither alone is enough — one dies on reinstall, the other on a new disk — and
/// together they miss only the person who replaces both at once, which is a new computer by any
/// reasonable definition.
/// </para>
/// <para>
/// What leaves this machine is a hash, never the serials themselves. Two reasons, and the second
/// matters more: a hash cannot be turned back into hardware identifiers if the licence database is
/// ever stolen, and it means Winora is not building a list of who owns which motherboard. The salt
/// is fixed and public — it is in this file — because its job is to keep the value specific to
/// Winora, not to keep it secret. Without it the same digest would be computable by anyone else who
/// hashed the same two strings.
/// </para>
/// <para>
/// Empty is a legitimate answer. A machine that will not report either identifier gets no
/// fingerprint, and the site treats a missing one as "do not enforce" rather than as a refusal —
/// see the comment on the server's <c>tokenMatchesHardware</c>. Refusing to run because a registry
/// read failed would punish the user for our caution.
/// </para>
/// </remarks>
public sealed partial class HardwareId : IHardwareId
{
    /// <summary>Keeps the digest specific to Winora. Public on purpose; see the remarks.</summary>
    private const string Salt = "winora.hardware.v1";

    private const string CryptographyKey = @"SOFTWARE\Microsoft\Cryptography";

    private readonly Lazy<string> _value;

    public HardwareId()
        : this(ReadMachineGuid, ReadSystemVolumeSerial)
    {
    }

    /// <param name="machineGuid">Reads the Windows installation identifier.</param>
    /// <param name="volumeSerial">Reads the system volume's serial number.</param>
    internal HardwareId(Func<string> machineGuid, Func<string> volumeSerial)
    {
        ArgumentNullException.ThrowIfNull(machineGuid);
        ArgumentNullException.ThrowIfNull(volumeSerial);

        // Computed once: it cannot change while the process runs, and both reads touch the
        // registry and the file system.
        _value = new Lazy<string>(
            () => Compute(machineGuid(), volumeSerial()),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Value => _value.Value;

    /// <summary>
    /// The fingerprint for two identifiers, or empty when neither could be read.
    /// </summary>
    /// <remarks>
    /// One of the two is enough. Requiring both would mean a machine that hides its volume serial —
    /// some virtual disks do — could never be licensed at all.
    /// </remarks>
    internal static string Compute(string machineGuid, string volumeSerial)
    {
        var parts = new[] { machineGuid, volumeSerial }
            .Select(static part => (part ?? string.Empty).Trim())
            .Where(static part => part.Length > 0)
            .ToArray();

        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Salt + "|" + string.Join('|', parts)));

        // Half the digest: 128 bits is far past any chance of two machines colliding, and a
        // shorter value is one less line wrapped in a support message.
        return Convert.ToHexString(digest)[..32].ToLowerInvariant();
    }

    /// <summary>
    /// The identifier Windows writes when it is installed.
    /// </summary>
    /// <remarks>
    /// Read from the 64-bit view explicitly. Winora is a 64-bit program, so it would land there
    /// anyway, but naming it means a future 32-bit build does not silently read the redirected
    /// <c>Wow6432Node</c> copy and produce a different fingerprint for the same machine.
    /// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/sysinfo/32-bit-and-64-bit-application-data
    /// </remarks>
    private static string ReadMachineGuid()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(CryptographyKey, writable: false);

            return key?.GetValue("MachineGuid") as string ?? string.Empty;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or global::System.Security.SecurityException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The serial of the volume Windows is installed on.
    /// </summary>
    /// <remarks>
    /// Taken from the system directory's root rather than from "C:", because Windows is not always
    /// on C: — it is on whatever letter it was installed to, and a machine that boots from D: would
    /// otherwise get the serial of some other disk, or none.
    /// </remarks>
    private static string ReadSystemVolumeSerial()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

            if (string.IsNullOrEmpty(root))
            {
                return string.Empty;
            }

            // Буферы имён не передаются вовсе: функция принимает для них NULL, а
            // нужен отсюда только серийный номер.
            if (!GetVolumeInformation(root, IntPtr.Zero, 0, out var serial, out _, out _,
                    IntPtr.Zero, 0))
            {
                return string.Empty;
            }

            return serial.ToString("x8", global::System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EntryPointNotFoundException)
        {
            return string.Empty;
        }
    }

    // Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumeinformationw
    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformation(
        string rootPathName,
        IntPtr volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        IntPtr fileSystemNameBuffer,
        int fileSystemNameSize);
}
