using System.Runtime.InteropServices;
using Winora.Core.Appearance;

namespace Winora.System.Windows;

/// <summary>
/// Whether Windows is running a High Contrast theme.
/// </summary>
/// <remarks>
/// <para>
/// Read through documented <c>SystemParametersInfoW</c> with <c>SPI_GETHIGHCONTRAST</c>:
/// <see href="https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow" />
/// and <see href="https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-highcontrastw" />.
/// </para>
/// <para>
/// It exists so the appearance layer can stand down. High Contrast is a user telling Windows they
/// need specific colours to see the screen at all; a decorative scheme painted over it is not a
/// preference being honoured, it is an accessibility setting being overridden. A probe that cannot
/// read the flag reports <c>true</c>, because standing down wrongly costs a colour scheme and
/// painting over it wrongly costs someone the ability to use the app.
/// </para>
/// </remarks>
public sealed partial class HighContrastProbe : IHighContrastProbe
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HighContrastOn = 0x00000001;

    /// <inheritdoc />
    public bool IsHighContrast()
    {
        var info = new HighContrastW
        {
            CbSize = (uint)Marshal.SizeOf<HighContrastW>(),
        };

        return !SystemParametersInfoW(SpiGetHighContrast, info.CbSize, ref info, 0)
            || (info.DwFlags & HighContrastOn) == HighContrastOn;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HighContrastW
    {
        public uint CbSize;
        public uint DwFlags;
        public nint LpszDefaultScheme;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(
        uint action,
        uint param,
        ref HighContrastW data,
        uint winIni);
}
