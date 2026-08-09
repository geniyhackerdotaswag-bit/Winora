using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

/// <summary>
/// Every theme dictionary must define the same keys.
/// </summary>
/// <remarks>
/// <para>
/// A key present in one theme and missing from another does not fail the build and does not throw at
/// runtime: WinUI falls back to whatever it can find, so the app simply renders wrong in that theme
/// and only in that theme. On a project developed in dark mode, a hole in the light dictionary is
/// invisible until someone switches.
/// </para>
/// <para>
/// This is not hypothetical. The light dictionary in <c>Palette.xaml</c> was destroyed once by an
/// over-greedy regular expression and had to be rewritten from scratch; nothing would have caught it
/// except opening the app in light mode. This test would have.
/// </para>
/// </remarks>
namespace Winora.Architecture.Tests;

public sealed class ThemeDictionaryTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string Root = FindRoot();

    /// <summary>
    /// High contrast is deliberately excluded: it is meant to substitute system colours rather than
    /// restate the palette, so demanding key-for-key parity there would force meaningless entries.
    /// </summary>
    private const string HighContrast = "HighContrast";

    public static TheoryData<string> ThemedDictionaries()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(Root, "src", "Winora.App", "Resources"),
                     "*.xaml",
                     SearchOption.AllDirectories))
        {
            if (ThemesIn(file).Count > 1)
            {
                data.Add(Path.GetRelativePath(Root, file));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ThemedDictionaries))]
    public void Every_theme_defines_the_same_keys(string relativePath)
    {
        var themes = ThemesIn(Path.Combine(Root, relativePath));
        var reference = themes["Default"];

        foreach (var (theme, keys) in themes)
        {
            if (theme is "Default" or HighContrast)
            {
                continue;
            }

            var missing = reference.Except(keys).Order().ToArray();
            var extra = keys.Except(reference).Order().ToArray();

            Assert.True(
                missing.Length == 0,
                $"{relativePath}: theme '{theme}' is missing {string.Join(", ", missing)}");

            Assert.True(
                extra.Length == 0,
                $"{relativePath}: theme '{theme}' defines {string.Join(", ", extra)}, which 'Default' does not");
        }
    }

    /// <summary>
    /// A palette with no keys would pass every comparison above without meaning anything.
    /// </summary>
    /// <remarks>
    /// High contrast is exempt, and an empty high-contrast dictionary is the correct design rather
    /// than an oversight: overriding nothing is what lets the system's own high-contrast colours
    /// through, which is the whole point of the mode.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ThemedDictionaries))]
    public void Every_theme_actually_defines_something(string relativePath)
    {
        foreach (var (theme, keys) in ThemesIn(Path.Combine(Root, relativePath)))
        {
            if (theme == HighContrast)
            {
                continue;
            }

            Assert.True(keys.Count > 0, $"{relativePath}: theme '{theme}' is empty");
        }
    }

    /// <summary>Theme name to the keys it defines, for one XAML file.</summary>
    private static Dictionary<string, IReadOnlySet<string>> ThemesIn(string path)
    {
        var themes = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        var dictionaries = XDocument.Load(path)
            .Descendants(Xaml + "ResourceDictionary.ThemeDictionaries")
            .Elements(Xaml + "ResourceDictionary");

        foreach (var dictionary in dictionaries)
        {
            var name = dictionary.Attribute(X + "Key")?.Value;
            if (name is null)
            {
                continue;
            }

            // Direct children only: a key nested inside a style belongs to that style, not the theme.
            themes[name] = dictionary.Elements()
                .Select(static element => element.Attribute(X + "Key")?.Value)
                .Where(static key => key is not null)
                .Select(static key => key!)
                .ToHashSet(StringComparer.Ordinal);
        }

        return themes;
    }

    private static string FindRoot([CallerFilePath] string path = "") =>
        Directory.GetParent(path)!.Parent!.Parent!.FullName;
}
