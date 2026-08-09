using Winora.App.Controls;
using Winora.App.Navigation;
using Xunit;

namespace Winora.App.Tests.Navigation;

/// <summary>
/// A route names its icon by catalog key, and the pane resolves that key at runtime.
/// </summary>
/// <remarks>
/// Nothing checked that the key existed. The bypass route asked for <c>"shield"</c>, which was
/// never in the catalog; the lookup returned false, the item was built without an icon, and it
/// shipped that way — a blank space in the pane where every neighbour had a mark, with no error
/// anywhere. These tests exist so an unknown key fails in the suite rather than on screen.
/// </remarks>
public sealed class IconCatalogTests
{
    private static readonly RouteRegistry Registry = RouteRegistry.Create();

    [Fact]
    public void Every_icon_a_route_asks_for_exists_in_the_catalog()
    {
        foreach (var route in Registry.Routes)
        {
            if (route.IconGlyphKey is null)
            {
                continue;
            }

            Assert.True(
                FluentIconCatalog.Keys.Contains(route.IconGlyphKey),
                $"Route '{route.Key}' asks for icon '{route.IconGlyphKey}', which the catalog does " +
                "not have. It would render with a blank space and no error.");
        }
    }

    /// <summary>
    /// Every pane item must carry one. A single item without an icon reads as broken rather than as
    /// a deliberate choice, which is exactly how the bypass route looked.
    /// </summary>
    [Fact]
    public void Every_item_shown_in_the_pane_names_an_icon()
    {
        foreach (var route in Registry.Routes.Where(static route =>
            route.Placement != RoutePlacement.RouteOnly))
        {
            Assert.False(
                string.IsNullOrWhiteSpace(route.IconGlyphKey),
                $"Pane item '{route.Key}' would sit in the pane with no icon.");
        }
    }

    /// <summary>
    /// A key resolves as a font glyph or as path data, never as both, and the two kinds are not
    /// interchangeable at the call site.
    /// </summary>
    [Fact]
    public void No_key_is_registered_as_both_a_glyph_and_a_path()
    {
        foreach (var key in FluentIconCatalog.Keys)
        {
            var isGlyph = FluentIconCatalog.TryGetGlyph(key, out _);
            var isPath = FluentIconCatalog.TryGetPathData(key, out _);
            Assert.True(isGlyph ^ isPath, $"Icon '{key}' resolves to {(isGlyph ? "both kinds" : "neither kind")}.");
        }
    }

    /// <summary>
    /// Path data is parsed at runtime, so a typo would surface as a missing icon rather than as a
    /// build error. The shape of the mini-language is checked here instead.
    /// </summary>
    [Fact]
    public void Path_data_is_plausible_mini_language_and_carries_no_markup()
    {
        foreach (var key in FluentIconCatalog.Keys.Where(static key =>
            FluentIconCatalog.TryGetPathData(key, out _)))
        {
            Assert.True(FluentIconCatalog.TryGetPathData(key, out var data));
            Assert.StartsWith("M", data, StringComparison.Ordinal);
            // It is interpolated into a XAML fragment to be parsed; a markup character there would
            // either break the parse or, worse, inject an element.
            Assert.DoesNotContain('<', data);
            Assert.DoesNotContain('>', data);
            Assert.DoesNotContain('&', data);
        }
    }

    /// <summary>The catalog must not accumulate entries no route asks for.</summary>
    [Fact]
    public void The_catalog_holds_no_icon_that_nothing_uses()
    {
        var used = Registry.Routes
            .Select(static route => route.IconGlyphKey)
            .Where(static key => key is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal([], FluentIconCatalog.Keys.Where(key => !used.Contains(key)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_unknown_key_is_refused_rather_than_returning_a_placeholder()
    {
        Assert.False(FluentIconCatalog.TryGetGlyph("no-such-icon", out _));
        Assert.False(FluentIconCatalog.TryGetPathData("no-such-icon", out _));
        Assert.Throws<KeyNotFoundException>(() => FluentIconCatalog.GetGlyph("no-such-icon"));
    }
}
