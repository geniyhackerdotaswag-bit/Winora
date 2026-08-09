using System.Runtime.InteropServices;
using System.Text;

namespace Winora.System.Windows;

/// <param name="Id">The scheme GUID.</param>
/// <param name="Name">The name Windows shows for it.</param>
/// <param name="IsActive">True for the scheme currently in force.</param>
public sealed record PowerScheme(Guid Id, string Name, bool IsActive);

/// <summary>Reads and switches the Windows power plan.</summary>
public interface IPowerSchemeAccess
{
    IReadOnlyList<PowerScheme> Schemes();

    bool Activate(Guid schemeId);
}

/// <summary>
/// The power plan, through the documented <c>powrprof</c> API.
/// </summary>
/// <remarks>
/// <para>
/// This is the one setting in the performance domain that genuinely trades power for speed, and it
/// is fully documented — no registry, so unlike the taskbar and sound domains it works today rather
/// than waiting on the package being signed.
/// </para>
/// <para>
/// Schemes are enumerated rather than hard-coded. The three Windows ships are the common case, but
/// laptop vendors add their own and a machine can have a scheme the user made themselves; listing
/// what is actually there avoids offering a plan this machine does not have.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powergetactivescheme
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powersetactivescheme
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerenumerate
/// </remarks>
public sealed partial class WindowsPowerSchemeAccess : IPowerSchemeAccess
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorNoMoreItems = 259;

    /// <summary>ACCESS_SCHEME: enumerate the plans themselves rather than their settings.</summary>
    private const uint AccessScheme = 16;

    public IReadOnlyList<PowerScheme> Schemes()
    {
        var active = ActiveSchemeId();
        var schemes = new List<PowerScheme>();

        for (uint index = 0; ; index++)
        {
            var size = (uint)Marshal.SizeOf<Guid>();
            var buffer = new byte[size];

            var status = PowerEnumerate(
                nint.Zero,
                nint.Zero,
                nint.Zero,
                AccessScheme,
                index,
                buffer,
                ref size);

            if (status == ErrorNoMoreItems)
            {
                break;
            }

            if (status != ErrorSuccess)
            {
                // A hive that stops answering yields the plans found so far rather than nothing:
                // a partial list is still usable, an empty screen is not.
                break;
            }

            var id = new Guid(buffer);
            var name = FriendlyName(id);
            if (name.Length > 0)
            {
                schemes.Add(new PowerScheme(id, name, id == active));
            }
        }

        return schemes;
    }

    public bool Activate(Guid schemeId)
    {
        try
        {
            var id = schemeId;
            return PowerSetActiveScheme(nint.Zero, ref id) == ErrorSuccess;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Guid ActiveSchemeId()
    {
        var pointer = nint.Zero;
        try
        {
            if (PowerGetActiveScheme(nint.Zero, out pointer) != ErrorSuccess || pointer == nint.Zero)
            {
                return Guid.Empty;
            }

            return Marshal.PtrToStructure<Guid>(pointer);
        }
        catch (Exception)
        {
            return Guid.Empty;
        }
        finally
        {
            if (pointer != nint.Zero)
            {
                LocalFree(pointer);
            }
        }
    }

    private static string FriendlyName(Guid schemeId)
    {
        try
        {
            // Two-call pattern: the first asks how many bytes the name needs.
            uint size = 0;
            var id = schemeId;
            PowerReadFriendlyName(nint.Zero, ref id, nint.Zero, nint.Zero, null, ref size);
            if (size == 0)
            {
                return string.Empty;
            }

            var buffer = new byte[size];
            if (PowerReadFriendlyName(nint.Zero, ref id, nint.Zero, nint.Zero, buffer, ref size) != ErrorSuccess)
            {
                return string.Empty;
            }

            return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerEnumerate(
        nint rootPowerKey,
        nint schemeGuid,
        nint subGroupOfPowerSettingsGuid,
        uint accessFlags,
        uint index,
        [Out] byte[] buffer,
        ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadFriendlyName(
        nint rootPowerKey,
        ref Guid schemeGuid,
        nint subGroupOfPowerSettingsGuid,
        nint powerSettingGuid,
        [Out] byte[]? buffer,
        ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(nint userRootPowerKey, ref Guid schemeGuid);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
