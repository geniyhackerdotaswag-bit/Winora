using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Winora.Architecture.Tests;

/// <summary>
/// No <c>Image.Source</c> may be bound to a raw string.
/// </summary>
/// <remarks>
/// <para>
/// This crashed the app twice. WinUI converts the bound value while the template is being built, an
/// empty string throws "the value cannot be converted to type ImageSource", and the exception lands
/// during layout where nothing catches it — so the window closes instead of showing a blank
/// picture. Collapsing the element does not help: <c>x:Bind</c> still evaluates a hidden element's
/// source.
/// </para>
/// <para>
/// The second occurrence was added minutes after the first was fixed, on the next line of the same
/// file, which is why this is a rule and not a note.
/// </para>
/// </remarks>
public sealed class ImageBindingTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void No_image_source_is_bound_to_a_bare_string()
    {
        var offenders = new List<string>();

        foreach (var page in Directory.EnumerateFiles(
                     Path.Combine(Root, "src", "Winora.App"),
                     "*.xaml",
                     SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(page);

            // Any Source binding without a converter. The lookbehind keeps ItemsSource out of it:
            // that one takes a collection and has nothing to do with image conversion.
            foreach (Match match in Regex.Matches(text, @"(?<!\w)Source=""\{x:Bind[^}]*\}"""))
            {
                if (!match.Value.Contains("Converter=", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(page)}: {match.Value}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    private static string FindRoot([CallerFilePath] string path = "") =>
        Directory.GetParent(path)!.Parent!.Parent!.FullName;
}
