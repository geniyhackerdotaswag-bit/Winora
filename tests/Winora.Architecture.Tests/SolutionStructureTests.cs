using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace Winora.Architecture.Tests;

public sealed class SolutionStructureTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Core_has_no_outer_layer_or_platform_package_reference()
    {
        var xml = XDocument.Load(Path.Combine(Root, "src", "Winora.Core", "Winora.Core.csproj"));
        var refs = xml.Descendants().Where(x => x.Name.LocalName is "ProjectReference" or "PackageReference")
            .Select(x => (string?)x.Attribute("Include") ?? string.Empty).ToArray();
        Assert.DoesNotContain(refs, x => x.Contains("Winora.", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, x => x.Contains("WindowsAppSDK", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, x => x.Contains("System.Text.Json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Core_source_remains_serializer_filesystem_and_platform_independent()
    {
        var forbidden = new[]
        {
            "System.Text.Json",
            "Winora.Infrastructure",
            "Winora.System",
            "Microsoft.UI.Xaml",
            "Microsoft.Windows.AppLifecycle",
            "Microsoft.Win32.Registry",
            "System.Management.Automation",
        };
        var sources = Directory.EnumerateFiles(
            Path.Combine(Root, "src", "Winora.Core"),
            "*.cs",
            SearchOption.AllDirectories);

        foreach (var source in sources)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFileName(source),
                    "AssemblyInfo.cs"))
            {
                continue;
            }

            var text = File.ReadAllText(source);
            Assert.DoesNotContain(
                forbidden,
                token => text.Contains(token, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("Winora.Infrastructure")]
    [InlineData("Winora.System")]
    public void Inner_layers_reference_core_and_nothing_else_from_the_solution(string project)
    {
        Assert.Equal(new[] { "Winora.Core" }, WinoraProjectReferences(project));
    }

    [Fact]
    public void ElevatedHost_references_the_three_inner_layers_and_never_the_app_or_winui()
    {
        Assert.Equal(
            new[] { "Winora.Core", "Winora.Infrastructure", "Winora.System" },
            WinoraProjectReferences("Winora.ElevatedHost"));
        Assert.DoesNotContain(
            PackageReferences("Winora.ElevatedHost"),
            x => x.Contains("WindowsAppSDK", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void App_references_the_three_inner_layers_and_never_the_elevated_host()
    {
        Assert.Equal(
            new[] { "Winora.Core", "Winora.Infrastructure", "Winora.System" },
            WinoraProjectReferences("Winora.App"));
    }

    [Fact]
    public void ViewModels_never_reference_infrastructure_or_system_directly()
    {
        var viewModels = Path.Combine(Root, "src", "Winora.App", "ViewModels");
        if (!Directory.Exists(viewModels))
        {
            return;
        }

        var forbidden = new[] { "Winora.Infrastructure", "Winora.System" };
        foreach (var source in Directory.EnumerateFiles(viewModels, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(source);
            Assert.DoesNotContain(
                forbidden,
                token => text.Contains(token, StringComparison.Ordinal));
        }
    }

    private static string[] WinoraProjectReferences(string project) =>
        References(project, "ProjectReference")
            .Select(static include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .Where(static name => name.StartsWith("Winora.", StringComparison.Ordinal))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

    private static string[] PackageReferences(string project) => References(project, "PackageReference");

    private static string[] References(string project, string elementName)
    {
        var xml = XDocument.Load(Path.Combine(Root, "src", project, project + ".csproj"));
        return xml.Descendants()
            .Where(x => x.Name.LocalName == elementName)
            .Select(x => (string?)x.Attribute("Include") ?? string.Empty)
            .ToArray();
    }

    private static string FindRoot([CallerFilePath] string sourceFile = "")
    {
        var candidates = new[]
        {
            Path.GetDirectoryName(sourceFile),
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        };

        foreach (var candidate in candidates.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            var directory = new DirectoryInfo(candidate!);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Winora.sln")) &&
                    File.Exists(Path.Combine(directory.FullName, "src", "Winora.Core", "Winora.Core.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Repository root not found from the source, working, or output directory.");
    }
}
