using System.Runtime.InteropServices;

namespace Winora.System.Windows;

/// <param name="Applied">Roles Windows accepted.</param>
/// <param name="Skipped">Roles whose file was missing, unreadable, or not settable.</param>
public readonly record struct CursorApplyResult(int Applied, int Skipped);

/// <summary>Replaces the system cursors, and puts them back.</summary>
public interface ICursorApplier
{
    CursorApplyResult Apply(IReadOnlyDictionary<CursorRole, string> files);

    /// <summary>Reloads the cursors Windows itself has on record, undoing any replacement.</summary>
    bool Restore();
}

/// <summary>
/// Applies a cursor scheme through documented Win32 calls only.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a registry write. <c>HKCU\Control Panel\Cursors</c> is not documented on
/// Microsoft Learn, and this package's registry writes are redirected into its own container while
/// it stays unsigned — a scheme written there would be applied to nothing and verified against
/// Winora's own copy. <c>LoadCursorFromFile</c> and <c>SetSystemCursor</c> are both documented and
/// touch no registry, so this works today and keeps working after signing.
/// </para>
/// <para>
/// The trade this makes, and the UI must say so: <c>SetSystemCursor</c> changes the running session
/// only. Signing out restores whatever the registry holds. That is a real limitation and also a
/// safety property — the worst case for a bad pack is "sign out and it is gone".
/// </para>
/// <para>
/// Undo is <c>SystemParametersInfo(SPI_SETCURSORS)</c>, which tells Windows to reload the cursors it
/// has on record. Winora therefore never needs to remember the previous images.
/// </para>
/// <para>
/// Three roles have no documented identifier and are skipped rather than approximated: handwriting,
/// location select and person select.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setsystemcursor
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-loadcursorfromfilew
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow
/// </remarks>
public sealed partial class WindowsCursorApplier : ICursorApplier
{
    private const uint SpiSetCursors = 0x0057;
    private const uint SpifSendChange = 0x0002;

    /// <summary>
    /// The documented OCR_* identifiers. Roles absent from this map cannot be set by
    /// <c>SetSystemCursor</c> at all, so Winora reports them skipped instead of pretending.
    /// </summary>
    private static readonly IReadOnlyDictionary<CursorRole, uint> RoleIds =
        new Dictionary<CursorRole, uint>
        {
            [CursorRole.Arrow] = 32512,
            [CursorRole.IBeam] = 32513,
            [CursorRole.Wait] = 32514,
            [CursorRole.Crosshair] = 32515,
            [CursorRole.UpArrow] = 32516,
            [CursorRole.SizeNWSE] = 32642,
            [CursorRole.SizeNESW] = 32643,
            [CursorRole.SizeWE] = 32644,
            [CursorRole.SizeNS] = 32645,
            [CursorRole.SizeAll] = 32646,
            [CursorRole.No] = 32648,
            [CursorRole.Hand] = 32649,
            [CursorRole.AppStarting] = 32650,
            [CursorRole.Help] = 32651,
        };

    public CursorApplyResult Apply(IReadOnlyDictionary<CursorRole, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var applied = 0;
        var skipped = 0;

        foreach (var (role, path) in files)
        {
            if (!RoleIds.TryGetValue(role, out var id) || !File.Exists(path))
            {
                skipped++;
                continue;
            }

            var cursor = LoadCursorFromFile(path);
            if (cursor == nint.Zero)
            {
                skipped++;
                continue;
            }

            // SetSystemCursor takes ownership of the handle and destroys it, so it is never freed
            // here. A failed call leaves the handle leaked for the process lifetime, which is the
            // documented behaviour and preferable to destroying a handle the system may now own.
            if (SetSystemCursor(cursor, id))
            {
                applied++;
            }
            else
            {
                skipped++;
            }
        }

        return new CursorApplyResult(applied, skipped);
    }

    public bool Restore() => SystemParametersInfoW(SpiSetCursors, 0, nint.Zero, SpifSendChange);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorFromFileW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadCursorFromFile(string fileName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSystemCursor(nint cursor, uint id);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint uiParam, nint pvParam, uint fWinIni);
}
