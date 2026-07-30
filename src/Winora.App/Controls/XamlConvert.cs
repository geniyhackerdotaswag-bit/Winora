using Microsoft.UI.Xaml;

namespace Winora.App.Controls;

/// <summary>
/// Small helpers for <c>x:Bind</c> function binding, so ViewModels can expose plain booleans instead
/// of WinUI <see cref="Visibility"/> values.
/// </summary>
public static class XamlConvert
{
    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
