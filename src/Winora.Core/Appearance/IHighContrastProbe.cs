namespace Winora.Core.Appearance;

/// <summary>
/// Whether the system is in a High Contrast theme, and Winora's own colours must therefore stand
/// down entirely.
/// </summary>
/// <remarks>
/// This is not one preference losing to another. High Contrast is a person telling Windows which
/// colours they can see; a scheme painted over it is an accessibility setting being overridden.
/// <c>Palette.xaml</c> already hands the whole High Contrast dictionary to the system, and the
/// runtime scheme has to respect the same boundary.
/// </remarks>
public interface IHighContrastProbe
{
    bool IsHighContrast();
}
