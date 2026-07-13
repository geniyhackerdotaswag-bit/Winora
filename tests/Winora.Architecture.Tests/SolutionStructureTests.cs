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

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Winora.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
