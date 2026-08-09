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

            if (!isShown && !isNavigatedTo)
            {
                orphans.Add(name);
            }
        }

        Assert.True(
            orphans.Count == 0,
            "These routes are neither shown nor navigated to: " + string.Join(", ", orphans.Order(StringComparer.Ordinal)));
    }

    private static IEnumerable<string> RouteKeyNames() =>
        Regex.Matches(
                File.ReadAllText(Navigation("RouteKeys.cs")),
                @"public const string (\w+) = ""[^""]+"";")
            .Select(static match => match.Groups[1].Value);

    private static string FindRoot([CallerFilePath] string path = "") =>
        Directory.GetParent(path)!.Parent!.Parent!.FullName;
}
