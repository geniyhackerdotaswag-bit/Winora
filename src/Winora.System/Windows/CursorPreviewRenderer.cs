using System.Runtime.InteropServices;

namespace Winora.System.Windows;

/// <param name="Width">Pixel width of the rendered image.</param>
/// <param name="Height">Pixel height of the rendered image.</param>
/// <param name="Bgra">Premultiplied BGRA pixels, top-down.</param>
public sealed record CursorPreview(int Width, int Height, byte[] Bgra);

/// <summary>Draws a cursor file into pixels a UI can show.</summary>
public interface ICursorPreviewRenderer
{
    CursorPreview? TryRender(string cursorPath, int size);
}

/// <summary>
/// Renders <c>.cur</c> and <c>.ani</c> files to bitmap pixels.
/// </summary>
/// <remarks>
/// <para>
/// Needed because WinUI's <c>Image</c> cannot decode either format. An earlier version bound the
/// file path straight to <c>Image.Source</c> on a claim that it could; it crashed the app the first
/// time the screen opened, with "the value cannot be converted to type ImageSource". Windows draws
/// these formats through the cursor API, never through an image decoder.
/// </para>
/// <para>
/// The drawing itself is shared with the icon preview: a cursor handle is an <c>HICON</c>, so only
/// how the handle is obtained differs. An <c>.ani</c> renders its first frame, which is exactly what
/// a still preview wants.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-loadcursorfromfilew
/// </remarks>
public sealed partial class CursorPreviewRenderer : ICursorPreviewRenderer
{
    public CursorPreview? TryRender(string cursorPath, int size)
    {
        if (string.IsNullOrWhiteSpace(cursorPath) || size <= 0 || !File.Exists(cursorPath))
        {
            return null;
        }

        var cursor = nint.Zero;
        try
        {
            cursor = LoadCursorFromFile(cursorPath);
            if (cursor == nint.Zero)
            {
                return null;
            }

            var pixels = IconRasterizer.Draw(cursor, size);
            return pixels is null ? null : new CursorPreview(size, size, pixels);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (cursor != nint.Zero)
            {
                DestroyCursor(cursor);
            }
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorFromFileW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadCursorFromFile(string fileName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyCursor(nint cursor);
}
