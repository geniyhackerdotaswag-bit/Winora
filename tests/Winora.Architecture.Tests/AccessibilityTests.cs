using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Winora.Architecture.Tests;

/// <summary>
/// What a screen reader is given to say.
/// </summary>
/// <remarks>
/// These are the parts of the accessibility pass that can be decided from the markup. Keyboard-only
/// navigation, High Contrast and 200% scaling still need a person at the screen; nothing here
/// pretends otherwise.
/// </remarks>
public sealed class AccessibilityTests
{
    private static readonly string Root = FindRoot();

    /// <summary>
    /// An icon is either decoration or information, and the markup has to say which.
    /// </summary>
    /// <remarks>
    /// Left unmarked, a screen reader announces an unlabelled graphic between every two useful
    /// sentences. Every icon in this app is decorative — the text beside it carries the meaning — so
    /// they are marked <c>Raw</c> and skipped. A named one is allowed too, for the case where an
    /// icon ever does carry meaning on its own.
    /// </remarks>
    [Fact]
    public void Every_icon_is_marked_decorative_or_named()
    {
        var offenders = new List<string>();

        foreach (var (file, element) in Elements("FontIcon", "Image"))
        {
            var isDecorative = element.Contains(
                @"AutomationProperties.AccessibilityView=""Raw""",
                StringComparison.Ordinal);

            var isNamed = element.Contains("AutomationProperties.Name", StringComparison.Ordinal);

            if (!isDecorative && !isNamed)
            {
                offenders.Add($"{Path.GetFileName(file)}: {Condense(element)}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Icons that a screen reader would announce as unlabelled:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A control the user can operate must say what it does.
    /// </summary>
    /// <remarks>
    /// Buttons in this app carry their label as content, which is announced automatically, so the
    /// rule only has teeth for one whose content is an icon or nothing at all — the shape that reads
    /// as "button" and no more.
    /// </remarks>
    [Fact]
    public void No_button_is_announced_without_a_label()
    {
        var offenders = new List<string>();

        foreach (var file in PageFiles())
        {
            var text = File.ReadAllText(file);

            var codeBehind = File.Exists(file + ".cs") ? File.ReadAllText(file + ".cs") : string.Empty;

            foreach (Match match in Regex.Matches(text, @"<Button\b(?<attributes>[^>]*?)(?<close>/>|>)", RegexOptions.Singleline))
            {
                var attributes = match.Groups["attributes"].Value;

                var hasContentAttribute = attributes.Contains("Content=", StringComparison.Ordinal);
                var hasName = attributes.Contains("AutomationProperties.Name", StringComparison.Ordinal);

                // An element with children carries its label inside, which is announced as content.
                var hasChildren = match.Groups["close"].Value == ">";

                // Some buttons take their label from the code-behind, which reads it from resources
                // just as a binding would. That is a labelled button, so it must not be flagged.
                var elementName = Regex.Match(attributes, @"x:Name=""(?<name>\w+)""").Groups["name"].Value;
                var labelledInCode = elementName.Length > 0 &&
                    codeBehind.Contains($"{elementName}.Content", StringComparison.Ordinal);

                if (!hasContentAttribute && !hasName && !hasChildren && !labelledInCode)
                {
                    offenders.Add($"{Path.GetFileName(file)}: {Condense(match.Value)}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Buttons with nothing to announce:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// User-facing text comes from resources, never from a literal in the markup.
    /// </summary>
    /// <remarks>
    /// A literal cannot be translated and, worse, cannot be reviewed alongside the rest of the
    /// wording. The rule is the specification's; this checks it rather than trusting it.
    /// </remarks>
    [Fact]
    public void No_page_hardcodes_user_facing_text()
    {
        var offenders = new List<string>();

        foreach (var file in PageFiles())
        {
            var text = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(text, @"\bText=""(?<literal>[^""{}]+)"""))
            {
                // A character entity is a symbol written the only way XML allows, not prose. The
                // arrow between a current and a proposed value is written "&#x2192;" and would
                // otherwise be flagged for containing the letter x.
                var literal = Regex
                    .Replace(match.Groups["literal"].Value, @"&#?\w+;", string.Empty)
                    .Trim();

                if (literal.Length > 1 && literal.Any(char.IsLetter))
                {
                    offenders.Add($"{Path.GetFileName(file)}: \"{literal}\"");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Hardcoded text that should live in Resources.resw:\n  " + string.Join("\n  ", offenders));
    }

    private static IEnumerable<(string File, string Element)> Elements(params string[] names)
    {
        var pattern = $@"<(?:{string.Join('|', names)})\b[^>]*?/?>";

        foreach (var file in PageFiles())
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), pattern, RegexOptions.Singleline))
            {
                yield return (file, match.Value);
            }
        }
    }

    private static IEnumerable<string> PageFiles() =>
        Directory
            .EnumerateFiles(Path.Combine(Root, "src", "Winora.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // Style dictionaries describe how controls look, not what any one of them says.
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}Resources{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>One line, short enough to read in a failure message.</summary>
    private static string Condense(string element)
    {
        var single = Regex.Replace(element, @"\s+", " ").Trim();
        return single.Length > 110 ? single[..110] + "…" : single;
    }

    private static string FindRoot([CallerFilePath] string path = "") =>
        Directory.GetParent(path)!.Parent!.Parent!.FullName;
}
