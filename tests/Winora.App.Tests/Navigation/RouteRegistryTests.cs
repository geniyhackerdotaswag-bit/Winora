using System.Text.RegularExpressions;
using Winora.App.Navigation;
using Xunit;

namespace Winora.App.Tests.Navigation;

public sealed class RouteRegistryTests
{
    /// <summary>
    /// Every route named by section 10 of the approved specification, including the route-only
    /// screens. A missing key must fail here rather than producing a silent dead click.
    /// </summary>
    public static readonly string[] SpecifiedRoutes =
    [
        "dashboard",
        "themes",
        "taskbar",
        "performance",
        "cleanup",
        "sounds",
        "cursors",
        "icons",
        "startup",
        "changes",
        "backups",
        "journal",
        "settings",
        "compatibility",
        "change-review",
        "rollback-review",
        "applying",
        "result-success",
        "result-failure",
        "result-partial-recovery",
        "recovery",
    ];

    private static readonly RouteRegistry Registry = RouteRegistry.Create();

    [Theory]
    [MemberData(nameof(SpecifiedRouteCases))]
    public void Every_specified_route_is_registered(string key)
    {
        Assert.True(Registry.TryFind(key, out var descriptor), $"Route '{key}' is not registered.");
        Assert.NotNull(descriptor);
        Assert.Equal(key, descriptor!.Key);
    }

    public static TheoryData<string> SpecifiedRouteCases()
    {
        var data = new TheoryData<string>();
        foreach (var route in SpecifiedRoutes)
        {
            data.Add(route);
        }

        return data;
    }

    [Fact]
    public void The_registry_contains_no_routes_beyond_the_specification()
    {
        var registered = Registry.Routes.Select(static route => route.Key).OrderBy(static key => key, StringComparer.Ordinal);
        var specified = SpecifiedRoutes.OrderBy(static key => key, StringComparer.Ordinal);
        Assert.Equal(specified, registered);
    }

    [Fact]
    public void Route_keys_are_unique()
    {
        var keys = Registry.Routes.Select(static route => route.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Route_keys_are_stable_lowercase_kebab_and_never_localized()
    {
        foreach (var route in Registry.Routes)
        {
            Assert.Matches(new Regex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$"), route.Key);
        }
    }

    [Fact]
    public void Every_route_carries_a_non_blank_title_resource_key()
    {
        foreach (var route in Registry.Routes)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(route.TitleResourceKey),
                $"Route '{route.Key}' has no title resource key.");
        }
    }

    [Fact]
    public void Title_resource_keys_are_unique_so_no_two_screens_share_a_heading()
    {
        var titles = Registry.Routes.Select(static route => route.TitleResourceKey).ToArray();
        Assert.Equal(titles.Length, titles.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_navigation_pane_item_declares_a_group_and_resolves_to_a_route()
    {
        var paneItems = Registry.Routes.Where(static route => route.Placement != RoutePlacement.RouteOnly).ToArray();
        Assert.NotEmpty(paneItems);

        foreach (var item in paneItems)
        {
            Assert.True(Registry.TryFind(item.Key, out _), $"Pane item '{item.Key}' does not resolve.");
            if (item.Placement == RoutePlacement.Pane)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(item.GroupResourceKey),
                    $"Pane item '{item.Key}' must belong to a titled group.");
            }
        }
    }

    [Fact]
    public void The_footer_holds_exactly_the_journal_and_settings_items()
    {
        var footer = Registry.Routes
            .Where(static route => route.Placement == RoutePlacement.Footer)
            .Select(static route => route.Key)
            .OrderBy(static key => key, StringComparer.Ordinal);
        Assert.Equal(new[] { "journal", "settings" }, footer);
    }

    [Fact]
    public void Route_only_screens_are_never_offered_in_the_pane()
    {
        var expected = new[]
        {
            "applying",
            "change-review",
            "compatibility",
            "recovery",
            "result-failure",
            "result-partial-recovery",
            "result-success",
            "rollback-review",
        };
        var routeOnly = Registry.Routes
            .Where(static route => route.Placement == RoutePlacement.RouteOnly)
            .Select(static route => route.Key)
            .OrderBy(static key => key, StringComparer.Ordinal);
        Assert.Equal(expected, routeOnly);
    }

    [Fact]
    public void An_unregistered_key_fails_loudly_rather_than_returning_a_default()
    {
        Assert.False(Registry.TryFind("no-such-route", out var missing));
        Assert.Null(missing);
        Assert.Throws<KeyNotFoundException>(() => Registry.Find("no-such-route"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_key_is_rejected(string? key)
    {
        Assert.False(Registry.TryFind(key!, out _));
    }

    [Fact]
    public void The_dashboard_is_the_single_start_route()
    {
        Assert.Equal("dashboard", Registry.StartRouteKey);
        Assert.True(Registry.TryFind(Registry.StartRouteKey, out _));
    }

    [Fact]
    public void RouteKeys_constants_and_the_registry_cannot_drift_apart()
    {
        Assert.Equal(
            Registry.Routes.Select(static route => route.Key).OrderBy(static key => key, StringComparer.Ordinal),
            RouteKeys.All.OrderBy(static key => key, StringComparer.Ordinal));
    }
}
