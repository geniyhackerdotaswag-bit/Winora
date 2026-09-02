using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Winora.App.Controls;

/// <summary>
/// Turns a published picture's address into something an <c>Image</c> can show.
/// </summary>
/// <remarks>
/// <para>
/// A converter rather than a direct binding, and that is a rule rather than a preference:
/// <c>Image.Source</c> bound to a bare string has closed this window twice. WinUI converts the value
/// while the template is being built, an empty string throws "the value cannot be converted to type
/// ImageSource", and the exception lands in layout where nothing catches it. Collapsing the element
/// does not help — <c>x:Bind</c> still evaluates a hidden element's source. See
/// <c>Winora.Architecture.Tests.ImageBindingTests</c>.
/// </para>
/// <para>
/// Anything that is not an absolute http or https address comes back as null, which shows an empty
/// frame. A relative path here would be resolved against the app package, and a manifest is a file
/// from the internet: it does not get to name a place inside the program's own files.
/// </para>
/// </remarks>
public sealed partial class RemoteImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string address ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            return new BitmapImage(uri);
        }
        catch (Exception)
        {
            // A picture that will not load is an empty frame on a card, and nothing more.
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
