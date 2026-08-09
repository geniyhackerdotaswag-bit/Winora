using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Winora.Architecture.Tests;

/// <summary>
/// Every stable reason code an adapter can hand to the UI must have a Russian string behind it.
/// <para>
/// This is a source-text rule for the same reason the ViewModel boundary is: it must not be able to
/// fail for environmental reasons. It exists because the failure it catches has already shipped
/// twice — a code with no string renders as the raw key in guillemets on the screen, and the only
/// way anyone noticed was by looking at the running app.
/// </para>
/// </summary>
public sealed class ReasonCodeLocalizationTests
{
    private static readonly string Root = FindRoot();

    /// <summary>
    /// Mirrors <c>ResourceLocalizationService.Get</c>: MRT treats a dot as a path separator, so
    /// dotted and hyphenated codes are flattened to underscores before lookup.
    /// </summary>
    private static string ResourceKeyFor(string reasonCode) =>
        reasonCode.Replace('.', '_').Replace('-', '_');

    [Fact]
    public void Every_winora_reason_code_in_the_system_layer_has_a_localized_string()
    {
        var declared = ResourceNames();

        var missing = ReasonCodes()
            .Where(entry => !declared.Contains(ResourceKeyFor(entry.Code)))
            .Select(entry => $"{entry.Code} -> {ResourceKeyFor(entry.Code)} ({entry.File})")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "Reason codes with no string:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>
    /// The rule above is worthless if it finds nothing to check, and for a long time it found
    /// nothing at all.
    /// </summary>
    /// <remarks>
    /// Every capability code is assembled as <c>Prefix + "…"</c>, so the whole literal never appears
    /// in the source the scan was reading. Ten codes went unchecked and all ten were missing their
    /// strings — the screen showed "[winora.capability.target-not-writable]" to the user while this
    /// test reported success. A test that cannot fail is worse than no test, because it is believed.
    /// </remarks>
    [Fact]
    public void The_scan_actually_finds_the_reason_codes()
    {
        var codes = ReasonCodes().Select(static entry => entry.Code).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("winora.capability.target-not-writable", codes);
        Assert.Contains("winora.cleanup.servicing-logs", codes);

        // Both families, so losing one of the two collection routes cannot go unnoticed.
        Assert.True(
            codes.Count(static code => code.StartsWith("winora.capability.", StringComparison.Ordinal)) >= 10,
            "Expected at least ten capability codes; found " + codes.Count);
    }

    /// <summary>
    /// Codes written whole, and codes assembled from a <c>Prefix</c> constant. Both shapes are in
    /// use, and reading only the first is what let ten codes ship unlocalized.
    /// </summary>
    private static IEnumerable<(string Code, string File)> ReasonCodes()
    {
        foreach (var source in Directory.EnumerateFiles(
                     Path.Combine(Root, "src", "Winora.System"),
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(source);
            var name = Path.GetFileName(source);

            // Anchored to the two namespaces reason codes live in, so operation identifiers in the
            // same files are not mistaken for them.
            foreach (Match match in Regex.Matches(text, @"""(winora\.(?:capability|cleanup)\.[a-z0-9\-]+)"""))
            {
                yield return (match.Groups[1].Value, name);
            }

            var prefix = Regex.Match(text, @"const string Prefix = ""(?<value>winora\.[a-z]+\.)"";");
            if (!prefix.Success)
            {
                continue;
            }

            foreach (Match match in Regex.Matches(text, @"Prefix \+\s*""(?<suffix>[a-z0-9\-]+)"""))
            {
                yield return (prefix.Groups["value"].Value + match.Groups["suffix"].Value, name);
            }
        }
    }

    private static HashSet<string> ResourceNames()
    {
        var resw = XDocument.Load(Path.Combine(
            Root, "src", "Winora.App", "Strings", "ru-RU", "Resources.resw"));

        return resw.Root!
            .Elements("data")
            .Select(static element => (string?)element.Attribute("name") ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FindRoot([CallerFilePath] string path = "") =>
        Directory.GetParent(path)!.Parent!.Parent!.FullName;
}
