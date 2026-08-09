using System.Runtime.InteropServices;

namespace Winora.System.Windows;

/// <summary>Whether this process holds administrative rights right now.</summary>
public interface IElevationProbe
{
    bool IsElevated { get; }
}

/// <summary>
/// Reads <c>TokenElevation</c> from the current process token.
/// </summary>
/// <remarks>
/// <para>
/// Measured on 2026-08-02, correcting an assumption this project had recorded twice: a packaged
/// MSIX app <em>can</em> be elevated. Launching <c>shell:AppsFolder\{AUMID}</c> with the
/// <c>runas</c> verb produced a process whose token reported <c>TokenElevation</c> non-zero.
/// </para>
/// <para>
/// <c>TokenElevation</c> is read directly rather than asking
/// <c>WindowsPrincipal.IsInRole(Administrator)</c>. The role check answers a question about group
/// membership, and a packaged app runs with an AppContainer token whose groups may be filtered, so
/// the two can disagree. <c>TokenElevation</c> is also the exact signal that was measured from
/// outside when this was verified, which means the app and the measurement cannot drift apart.
/// </para>
/// <para>
/// Captured once at construction: elevation cannot change during the life of a process, and
/// re-reading it per call would invite callers to treat it as something that varies.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-gettokeninformation
/// </remarks>
public sealed partial class WindowsElevationProbe : IElevationProbe
{
    private const int TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;

    public WindowsElevationProbe()
    {
        IsElevated = Read();
    }

    public bool IsElevated { get; }

    private static bool Read()
    {
        var token = nint.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
            {
                return false;
            }

            if (!GetTokenInformation(token, TokenElevationClass, out var elevation, sizeof(uint), out _))
            {
                return false;
            }

            return elevation != 0;
        }
        catch (Exception)
        {
            // An unreadable token is treated as not elevated: Winora then offers less, which is the
            // safe direction to be wrong in.
            return false;
        }
        finally
        {
            if (token != nint.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, int desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        out uint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
