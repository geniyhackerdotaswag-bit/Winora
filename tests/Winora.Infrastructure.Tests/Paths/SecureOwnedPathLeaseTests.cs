using System.Diagnostics;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Tests.Persistence;
using Xunit;

namespace Winora.Infrastructure.Tests.Paths;

public sealed class SecureOwnedPathLeaseTests
{
    [Fact]
    public void Existing_directory_acquisition_does_not_create_a_missing_path()
    {
        using var root = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var missing = Path.Combine(paths.RootDirectory, "missing", "nested");

        Assert.Throws<DirectoryNotFoundException>(() =>
            SecureOwnedPathLease.AcquireExistingDirectory(paths, missing));

        Assert.False(Directory.Exists(missing));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Fact]
    public void Existing_directory_acquisition_rejects_a_junction_ancestor()
    {
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var linkedDirectory = Path.Combine(paths.RootDirectory, "linked");
        CreateJunction(linkedDirectory, outside.Path);

        try
        {
            Assert.ThrowsAny<IOException>(() =>
                SecureOwnedPathLease.AcquireExistingDirectory(paths, linkedDirectory));
        }
        finally
        {
            Directory.Delete(linkedDirectory);
        }
    }

    [Fact]
    public void Existing_directory_acquisition_pins_each_owned_ancestor()
    {
        using var root = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var parent = Path.Combine(paths.RootDirectory, "parent");
        var nested = Path.Combine(parent, "nested");
        Directory.CreateDirectory(nested);

        using (SecureOwnedPathLease.AcquireExistingDirectory(paths, nested))
        {
            Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-moved"));
        }

        Directory.Move(parent, parent + "-moved");
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo(
            "cmd.exe",
            $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Unable to start junction helper.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }
}
