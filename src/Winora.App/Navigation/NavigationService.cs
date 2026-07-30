using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Winora.App.Navigation;

/// <inheritdoc />
public sealed class NavigationService : INavigationService
{
    private readonly RouteRegistry _routes;
    private Frame? _frame;

    public NavigationService(RouteRegistry routes)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
    }

    public string? CurrentRouteKey { get; private set; }

    public event EventHandler<string>? Navigated;

    /// <summary>Called once by the shell. The Frame never leaks past this class.</summary>
    public void Attach(Frame frame)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public void NavigateTo(string routeKey, object? parameter = null)
    {
        // Find throws for an unregistered key, so a typo surfaces immediately instead of leaving the
        // frame on the previous page and looking like an ignored click.
        var route = _routes.Find(routeKey);

        if (_frame is null)
        {
            throw new InvalidOperationException("The navigation service has no frame attached.");
        }

        if (string.Equals(CurrentRouteKey, route.Key, StringComparison.Ordinal))
        {
            return;
        }

        _frame.Navigate(
            PageCatalog.PageTypeFor(route.Key),
            parameter ?? route.Key,
            new EntranceNavigationTransitionInfo());

        CurrentRouteKey = route.Key;
        Navigated?.Invoke(this, route.Key);
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true)
        {
            _frame.GoBack();
        }
    }
}
