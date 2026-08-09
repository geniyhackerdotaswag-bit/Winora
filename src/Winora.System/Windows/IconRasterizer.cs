using System.Runtime.InteropServices;

namespace Winora.System.Windows;

/// <summary>
/// Draws a cursor or icon handle into pixels a UI can show.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the renderer that obtains the handle, because <c>DrawIconEx</c> takes an
/// <c>HICON</c> and a cursor handle is one — the drawing does not care which it was given.
/// </para>
/// <para>
/// The target is a 32-bit top-down DIB, so the rows come out in the order a UI expects and the
/// alpha channel survives — an icon composited onto a white background would have a white fringe
/// on every rounded edge.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-drawiconex
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-createdibsection
/// </remarks>
internal static partial class IconRasterizer
{
    private const uint DiNormal = 0x0003;
    private const int BiRgb = 0;
    private const uint DibRgbColors = 0;

    /// <summary>
    /// Returns premultiplied BGRA pixels, or null when the handle draws nothing visible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drawn twice, over black and over white, and the alpha channel is derived from the difference.
    /// Reading the alpha bytes back from a single pass does not work: a cursor stored in the older
    /// mask-and-colour format has no alpha of its own, and <c>DrawIconEx</c> composites it with
    /// raster operations that never touch the alpha bytes — they stay zero. Measured on a real pack
    /// whose every frame came back fully transparent while the cursor loaded and drew fine.
    /// </para>
    /// <para>
    /// The arithmetic: over black a pixel lands at <c>C·a</c>, over white at <c>C·a + 255·(1−a)</c>.
    /// Their difference gives <c>a</c>, and the black pass is already the premultiplied colour that
    /// a bitmap wants. One path covers both cursor formats, so there is no format sniffing to get
    /// wrong.
    /// </para>
    /// </remarks>
    internal static byte[]? Draw(nint icon, int size)
    {
        if (icon == nint.Zero || size <= 0)
        {
            return null;
        }

        try
        {
            var overBlack = DrawOver(icon, size, background: 0x00);
            var overWhite = DrawOver(icon, size, background: 0xFF);
            if (overBlack is null || overWhite is null)
            {
                return null;
            }

            var pixels = new byte[overBlack.Length];
            var hasContent = false;

            for (var index = 0; index < pixels.Length; index += 4)
            {
                // In theory identical on every channel; the largest is taken so a rounding
                // difference in one channel cannot make an opaque pixel look transparent.
                var lost = Math.Max(
                    Math.Max(overWhite[index] - overBlack[index], overWhite[index + 1] - overBlack[index + 1]),
                    overWhite[index + 2] - overBlack[index + 2]);

                var alpha = 255 - Math.Clamp(lost, 0, 255);
                if (alpha == 0)
                {
                    continue;
                }

                hasContent = true;
                pixels[index] = overBlack[index];
                pixels[index + 1] = overBlack[index + 1];
                pixels[index + 2] = overBlack[index + 2];
                pixels[index + 3] = (byte)alpha;
            }

            // A cursor that is transparent everywhere has nothing worth showing, and an empty card
            // reads as a broken one.
            return hasContent ? pixels : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <param name="background">Byte every channel is filled with before the icon is drawn.</param>
    private static byte[]? DrawOver(nint icon, int size, byte background)
    {
        var screenDc = nint.Zero;
        var memoryDc = nint.Zero;
        var bitmap = nint.Zero;
        var previous = nint.Zero;

        try
        {
            screenDc = GetDC(nint.Zero);
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == nint.Zero)
            {
                return null;
            }

            // Negative height requests top-down rows.
            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = size,
                Height = -size,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb,
            };

            bitmap = CreateDIBSection(memoryDc, ref header, DibRgbColors, out var bits, nint.Zero, 0);
            if (bitmap == nint.Zero || bits == nint.Zero)
            {
                return null;
            }

            var buffer = new byte[size * size * 4];
            Array.Fill(buffer, background);
            Marshal.Copy(buffer, 0, bits, buffer.Length);

            previous = SelectObject(memoryDc, bitmap);
            if (!DrawIconEx(memoryDc, 0, 0, icon, size, size, 0, nint.Zero, DiNormal))
            {
                return null;
            }

            Marshal.Copy(bits, buffer, 0, buffer.Length);
            return buffer;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (previous != nint.Zero)
            {
                SelectObject(memoryDc, previous);
            }

            if (bitmap != nint.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memoryDc != nint.Zero)
            {
                DeleteDC(memoryDc);
            }

            if (screenDc != nint.Zero)
            {
                ReleaseDC(nint.Zero, screenDc);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public int Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint gdiObject);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(
        nint dc,
        ref BitmapInfoHeader header,
        uint usage,
        out nint bits,
        nint section,
        uint offset);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawIconEx(
        nint dc,
        int x,
        int y,
        nint icon,
        int width,
        int height,
        uint step,
        nint brush,
        uint flags);
}
