using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Winora.System.Windows;

namespace Winora.App.Controls;

/// <summary>
/// Turns a cursor file path into something <c>Image.Source</c> can actually show.
/// </summary>
/// <remarks>
/// <para>
/// Binding the path straight to <c>Image.Source</c> crashed the app: WinUI has no decoder for
/// <c>.cur</c> or <c>.ani</c>, and the binding threw "the value cannot be converted to type
/// ImageSource" while the page was still building itself. The cursor has to be drawn through the
/// cursor API and handed over as pixels.
/// </para>
/// <para>
/// A converter rather than a property on the view model: the rendered image is a WinUI type, and
/// view models in this project do not touch WinUI.
/// </para>
/// </remarks>
public sealed partial class CursorPreviewConverter : IValueConverter
{
    private const int PreviewSize = 32;

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string path || path.Length == 0)
        {
            return null;
        }

        try
        {
            var renderer = App.Services.GetRequiredService<ICursorPreviewRenderer>();
            var preview = renderer.TryRender(path, PreviewSize);
            if (preview is null)
            {
                return null;
            }

            var bitmap = new WriteableBitmap(preview.Width, preview.Height);

            // Written straight into the buffer the bitmap already owns. The stream extension used
            // first does not exist for IBuffer, only for IRandomAccessStream.
            preview.Bgra.CopyTo(bitmap.PixelBuffer);

            return bitmap;
        }
        catch (Exception)
        {
            // No preview is a card without a picture. An exception here would be a card that takes
            // the whole screen down with it, which is what this converter exists to prevent.
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("A cursor preview is never converted back.");
}
