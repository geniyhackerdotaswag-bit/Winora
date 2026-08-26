using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Winora.Architecture.Tests;

/// <summary>
/// Every route resolves to a real page.
/// </summary>
/// <remarks>
/// <para>
/// A route with no entry in <c>PageCatalog</c> falls through to the placeholder, which says the
/// section is not built. That is honest while a section genuinely is not built, and it is a dead
/// click once the app ships — worst of all for <c>recovery</c>, which is where three of the worst
/// outcomes send a user who has just had a change go wrong.
/// </para>
/// <para>
/// Read from source text rather than by loading the assembly: <c>PageCatalog</c> names WinUI page
/// types, and touching that assembly from a plain test host fails on activation. Text cannot fail
/// for environmental reasons.
/// </para>
/// </remarks>
public sealed class NoDeadRouteTests
{
    private static readonly string Root = FindRoot();

    private static string Navigation(string file) =>
        Path.Combine(Root, "src", "Winora.App", "Navigation", file);

    [Fact]
    public void Every_route_key_resolves_to_a_page()
    {
        var catalog = File.ReadAllText(Navigation("PageCatalog.cs"));

        var missing = RouteKeyNames()
            .Where(name => !catalog.Contains($"RouteKeys.{name} =>", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "These routes fall through to the placeholder: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Sections taken out of the pane on purpose, which nothing therefore reaches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both were closed for technical work before 2026-08-26 and spent that time as pane items
    /// whose only content was the sentence "this section is closed". The owner had them taken out
    /// of the pane, which leaves them reachable by nothing — exactly the shape this test calls
    /// dead. The distinction it is drawing is between configuration that accumulated because
    /// nobody noticed and configuration parked on purpose, and only a written list can tell those
    /// two apart.
    /// </para>
    /// <para>
    /// The page, its strings and its tests stay whole, so returning a section to the pane is one
    /// word in <c>RouteRegistry</c> and one line removed from here. A name that stays on this list
    /// without either of those happening is the thing this test was written to catch, so it should
    /// be read as a debt and not as an exemption.
    /// </para>
    /// </remarks>
    private static readonly string[] Parked = ["Sounds", "Performance"];

    /// <summary>
    /// A route nothing navigates to is dead configuration, and it accumulates: two such routes were
    /// carried for weeks, each with a name, an icon and a resource string behind it.
    /// </summary>
    [Fact]
    public void Every_route_key_is_either_shown_or_navigated_to()
    {
        var registry = File.ReadAllText(Navigation("RouteRegistry.cs"));

        var sources = Directory
            .EnumerateFiles(Path.Combine(Root, "src", "Winora.App"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => !path.EndsWith("RouteKeys.cs", StringComparison.Ordinal))
            .Where(static path => !path.EndsWith("RouteRegistry.cs", StringComparison.Ordinal))
            .Where(static path => !path.EndsWith("PageCatalog.cs", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();

        var orphans = new List<string>();

        foreach (var name in RouteKeyNames())
        {
            // Shown in the shell, or reached from code. Either justifies the route existing.
            var isShown = Regex.IsMatch(
                registry,
                $@"RouteKeys\.{name},[^\n]*RoutePlacement\.(Pane|PaneRoot|Footer)");

            var isNavigatedTo = sources.Any(source =>
                source.Contains($"RouteKeys.{name}", StringComparison.Ordinal));

            if (!isShown && !isNavigatedTo && !Parked.Contains(name, StringComparer.Ordinal))
            {
                orphans.Add(name);
            }
        }

        Assert.True(
            orphans.Count == 0,
            "These routes are neither shown nor navigated to: " + string.Join(", ", orphans.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// Parking is a state a route can leave, so what is parked must still be a route.
    /// </summary>
    /// <remarks>
    /// The exemption above buys silence from one test; it does not buy silence from all of them.
    /// A parked name that no longer matches a route key is a list gone stale, and a stale list is
    /// how the exemption would quietly widen to cover something nobody parked.
    /// </remarks>
    [Fact]
    public void Every_parked_route_is_still_a_route()
    {
        var keys = RouteKeyNames().ToHashSet(StringComparer.Ordinal);

        Assert.All(Parked, name => Assert.Contains(name, keys));
    }

    private static IEnumerable<string> RouteKeyNames() =>
        Regex.Matches(
                File.ReadAllText(Navigation("RouteKeys.cs")),
                @"public const string (\w+) = ""[^""]+"";")
            .Select(static match => match.Groups[1].Value);

    private static string FindRoot([CallerFilePath] string path = "") =>
        Directory.GetParent(path)!.Parent!.Parent!.FullName;
}
