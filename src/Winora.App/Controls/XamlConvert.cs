using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Winora.App.Controls;

/// <summary>
/// Small helpers for <c>x:Bind</c> function binding, so ViewModels can expose plain booleans instead
/// of WinUI <see cref="Visibility"/> values.
/// </summary>
public static class XamlConvert
{
    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Informational when the condition holds, a warning when it does not.</summary>
    public static InfoBarSeverity InfoSeverity(bool isHealthy) =>
        isHealthy ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
}
