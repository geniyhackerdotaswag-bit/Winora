using System.Runtime.InteropServices;

namespace Winora.App.Diagnostics;

/// <summary>
/// Tells the user, in the one situation where nothing else can.
/// </summary>
/// <remarks>
/// <para>
/// When composition fails there is no window, no resource loader and no service provider, so none of
/// the app's own machinery is available to report it. Rethrowing simply ended the process: clicking
/// the shortcut did nothing at all, with no message and nothing in the Windows event log. That
/// happened for real on 2026-08-04, and the cause was already sitting in the diagnostic log the
/// whole time — the user just had no way to know the log existed.
/// </para>
/// <para>
/// A plain Win32 message box needs none of the app's own infrastructure, which is exactly why it is
/// the right tool here. The text is not in <c>.resw</c> for the same reason: the resource subsystem
/// is part of what may have failed.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-messageboxw
/// </remarks>
internal static class StartupFailureNotice
{
    private const uint IconError = 0x00000010;
    private const uint TopMost = 0x00040000;

    internal static void Show(string logPath)
    {
        try
        {
            var text =
                "Winora не смогла запуститься.\n\n" +
                "Причина записана в файл:\n" + logPath + "\n\n" +
                "Приложение ничего не изменило в системе: сбой произошёл до начала работы.";

            MessageBox(nint.Zero, text, "Winora", IconError | TopMost);
        }
        catch (Exception)
        {
            // Reporting the failure must never become a second failure.
        }
    }

    // DllImport rather than LibraryImport: the source generator emits unsafe code, which this
    // project does not allow, and one message box is not worth relaxing that for.
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);
}
