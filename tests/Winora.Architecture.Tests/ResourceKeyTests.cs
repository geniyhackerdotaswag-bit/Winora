using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Winora.Architecture.Tests;

/// <summary>
/// Every resource key the app asks for exists.
/// </summary>
/// <remarks>
/// <para>
/// A missing key does not fail the build and does not throw. The localizer returns a visible marker
/// and the user reads "[winora.cleanup.windows-serviced]" off the screen, which is exactly what
/// happened once. A typo in a key is therefore a shipping bug that only a person looking at the
/// right screen would ever catch — unless a test catches it first.
/// </para>
/// <para>
/// Source text rather than reflection: the resources are compiled into a package resource index that
/// a plain test host cannot load, and the calls live in a WinUI assembly it cannot activate.
/// </para>
/// </remarks>
public sealed class ResourceKeyTests
{
    private static readonly string Root = FindRoot();

    private static readonly HashSet<string> Defined = DefinedKeys();

    [Fact]
    public void Every_requested_resource_key_is_defined()
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (file, key) in RequestedKeys())
        {
            if (!Defined.Contains(key))
            {
                missing.Add($"{key}  ({Path.GetFileName(file)})");
            }
        }

        Assert.True(missing.Count == 0, "Missing resource keys:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>
    /// A key is worthless if its value is empty: the localizer resolves it to the raw key and the
    /// screen shows the identifier to the user. That is strictly worse than a missing string,
    /// because it looks deliberate.
    /// </summary>
    [Fact]
    public void No_defined_resource_has_an_empty_value()
    {
        var empty = ResourceElements()
            .Where(static element =>
                string.IsNullOrWhiteSpace(element.Element("value")?.Value))
            .Select(static element => element.Attribute("name")!.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(empty.Length == 0, "Resources with an empty value: " + string.Join(", ", empty));
    }

    /// <summary>
    /// Keys built by appending an enum member — the journal shows category, status and risk this way.
    /// Every member must have a string, or one uncommon outcome renders as an identifier.
    /// </summary>
    [Theory]
    [InlineData("Journal_Category_", "ActionJournalCategory")]
    [InlineData("Journal_Status_", "ActionJournalStatus")]
    [InlineData("Journal_Risk_", "ActionJournalRisk")]
    public void Every_member_of_a_key_forming_enum_has_a_string(string prefix, string enumName)
    {
        var missing = EnumMembers(enumName)
            .Where(member => !Defined.Contains(prefix + member))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"'{enumName}' members with no '{prefix}*' string: " + string.Join(", ", missing));
    }

    /// <summary>A literal <c>Get("Key")</c> in C#.</summary>
    /// <remarks>
    /// Only literal arguments. A key assembled at run time cannot be checked this way, and the enum
    /// theory above covers the families that are.
    /// </remarks>
    private const string CodeCall = @"Get\(""([A-Za-z0-9_.]+)""\)";

    /// <summary>A key reaching the localizer from markup instead.</summary>
    /// <remarks>
    /// <para>
    /// The registration window binds its labels as <c>{x:Bind Localized('Reg_Next')}</c> rather than
    /// holding a property per label on a view model whose whole point is that it holds no view. That
    /// puts about thirty keys in a .xaml file, passed as a method argument, in single quotes — three
    /// separate reasons the scan above cannot see them, so until this pattern existed nobody was
    /// checking any of them.
    /// </para>
    /// <para>
    /// A key nobody checks is the failure this whole class was written for: a typo produces a blank
    /// label on the first screen the program ever shows, and nothing fails anywhere.
    /// </para>
    /// <para>
    /// Both quote characters are accepted because XAML allows either around an attribute value, and
    /// the inner literal has to use whichever one the attribute did not.
    /// </para>
    /// </remarks>
    private const string MarkupCall = @"Localized\(\s*['""]([A-Za-z0-9_.]+)['""]\s*\)";

    private static IEnumerable<(string File, string Key)> RequestedKeys() =>
        Requested("*.cs", CodeCall).Concat(Requested("*.xaml", MarkupCall));

    private static IEnumerable<(string File, string Key)> Requested(string files, string call)
    {
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(Root, "src", "Winora.App"),
                     files,
                     SearchOption.AllDirectories))
        {
            // obj holds the XAML compiler's own copy of every page and the code it generates from
            // them, so everything in there is a second sighting of a key already counted.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in Regex.Matches(File.ReadAllText(file), call))
            {
                yield return (file, match.Groups[1].Value);
            }
        }
    }

    private static IEnumerable<string> EnumMembers(string enumName)
    {
        var file = Directory
            .EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .FirstOrDefault(path => File.ReadAllText(path).Contains($"enum {enumName}", StringComparison.Ordinal));

        Assert.NotNull(file);

        var body = Regex.Match(
            File.ReadAllText(file),
            $@"enum {enumName}\s*\{{(?<body>[^}}]*)\}}",
            RegexOptions.Singleline);

        Assert.True(body.Success, $"Could not read the body of enum '{enumName}'.");

        return Regex.Matches(body.Groups["body"].Value, @"^\s*(\w+)\s*(?:=[^,]*)?,", RegexOptions.Multiline)
            .Select(static match => match.Groups[1].Value);
    }

    private static IEnumerable<XElement> ResourceElements() =>
        XDocument
            .Load(Path.Combine(Root, "src", "Winora.App", "Strings", "ru-RU", "Resources.resw"))
            .Root!
            .Elements("data")
            .Where(static element => element.Attribute("name") is not null);

    private static HashSet<string> DefinedKeys() =>
        ResourceElements()
            .Select(static element => element.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string FindRoot([CallerFilePath] string path = "") =>
        Directory.GetParent(path)!.Parent!.Parent!.FullName;
}
