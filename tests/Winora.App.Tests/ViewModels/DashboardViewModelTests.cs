using Winora.App.Navigation;
using Winora.App.Services;
using Winora.App.ViewModels;
using Xunit;

namespace Winora.App.Tests.ViewModels;

/// <summary>
/// The dashboard's four tiles.
/// </summary>
/// <remarks>
/// Read the remark on <see cref="DashboardViewModel"/> before adding a fifth, or anything beside
/// them. That screen has been emptied twice, and a list of recent changes was built at the owner's
/// request and removed the next day for reading as clutter. The test it keeps failing is whether a
/// thing is what a person arrived wanting or what the app wanted to say.
/// </remarks>
public sealed class DashboardViewModelTests
{
    /// <summary>The key comes back, so an assertion names the key rather than a translation.</summary>
    private sealed class EchoLocalization : ILocalizationService
    {
        public bool IsAvailable => true;

        public string Get(string resourceKey) => resourceKey;
    }

    /// <summary>Nothing left unfinished, so the recovery warning stays out of the way.</summary>
    private sealed class QuietRecovery : IRecoveryState
    {
        public Task<int> PendingCountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<RecoveryOutcome> RecoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecoveryOutcome(0, 0, string.Empty));
    }

    private static readonly RouteRegistry Registry = RouteRegistry.Create();

    private static DashboardViewModel Build() =>
        new(new QuietRecovery(), new EchoLocalization(), RouteRegistry.Create());

    [Fact]
    public async Task The_dashboard_offers_four_quick_actions()
    {
        var vm = Build();
        await vm.LoadAsync();

        Assert.Equal(4, vm.QuickActions.Count);
    }

    /// <summary>
    /// The tile does not hold its own name and icon; it asks the registry that builds the pane.
    /// A renamed section must not be called one thing on the left and another in the middle.
    /// </summary>
    [Fact]
    public async Task A_tile_takes_its_name_and_icon_from_the_route_registry()
    {
        var vm = Build();
        await vm.LoadAsync();

        Assert.NotEmpty(vm.QuickActions);

        foreach (var action in vm.QuickActions)
        {
            var route = Registry.Find(action.RouteKey);

            Assert.Equal(route.TitleResourceKey, action.Title);
            Assert.Equal(route.IconGlyphKey, action.IconGlyphKey);
        }
    }

    /// <summary>Names alone would make the tiles a copy of the pane standing to their right.</summary>
    [Fact]
    public async Task Every_tile_says_something_the_pane_does_not()
    {
        var vm = Build();
        await vm.LoadAsync();

        Assert.All(vm.QuickActions, action =>
            Assert.False(string.IsNullOrWhiteSpace(action.Description)));

        Assert.Equal(
            vm.QuickActions.Count,
            vm.QuickActions.Select(action => action.Description).Distinct(StringComparer.Ordinal).Count());

        Assert.All(vm.QuickActions, action => Assert.NotEqual(action.Title, action.Description));
    }

    /// <summary>A tile whose route is unregistered is a click that silently does nothing.</summary>
    [Fact]
    public async Task Every_tile_points_at_a_registered_route()
    {
        var vm = Build();
        await vm.LoadAsync();

        Assert.All(vm.QuickActions, action => Assert.True(Registry.TryFind(action.RouteKey, out _)));
    }

    /// <summary>
    /// A tile leading to a section closed for maintenance would be the dead end we just took out
    /// of the pane, put back on the screen in front of it.
    /// </summary>
    [Fact]
    public async Task No_tile_leads_to_a_section_that_is_closed()
    {
        var vm = Build();
        await vm.LoadAsync();

        Assert.All(vm.QuickActions, action =>
            Assert.NotEqual(RoutePlacement.RouteOnly, Registry.Find(action.RouteKey).Placement));
    }
}
