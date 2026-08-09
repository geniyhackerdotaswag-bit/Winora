using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Winora.App.Controls;

/// <summary>
/// Builds a <see cref="Geometry" /> from path mini-language, one fresh instance per call.
/// </summary>
/// <remarks>
/// <para>
/// Fresh per call is the whole point. A <c>Geometry</c> held in a <c>ResourceDictionary</c> is a
/// single shared object, and WinUI refuses to attach one to <c>PathIcon.Data</c>: the assignment
/// throws <c>E_INVALIDARG</c> and, because the pane is built in the <c>MainWindow</c> constructor,
/// the app failed to open at all. Measured on 2026-08-07 against 0.2.2.0.
/// </para>
/// <para>
/// WinUI exposes no <c>Geometry.Parse</c>, so the mini-language is handed to the XAML parser, which
/// is the documented way to reach that converter from code.
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/apps/design/controls/shapes
/// </para>
/// </remarks>
public static class IconGeometry
{
    private const string Namespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <returns>A new geometry, or null when the data is not valid path mini-language.</returns>
    public static Geometry? FromPathData(string pathData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathData);

        // The catalog is the only source of this text and holds no markup characters, so there is
        // nothing to escape. Anything that is not a well-formed path fails the parse and returns
        // null rather than producing a half-drawn icon.
        try
        {
            return XamlReader.Load($"<Geometry xmlns='{Namespace}'>{pathData}</Geometry>") as Geometry;
        }
        catch (Exception exception) when (exception is XamlParseException or ArgumentException)
        {
            return null;
        }
    }
}
