using System.Runtime.InteropServices;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Xunit;

namespace Winora.Core.Tests.Changes;

public sealed class BackupCaptureContractTests
{
    [Fact]
    public void Artifact_and_capture_defensively_own_opaque_bytes_and_collection()
    {
        var source = new byte[] { 1, 2, 3 };
        var artifact = BackupArtifact.Create(
            "startup/run-value",
            "registry-value",
            source);
        var artifacts = new List<BackupArtifact> { artifact };
        var capture = BackupCapture.ForOperation(
            Fingerprint("source"),
            Fingerprint("source"),
            artifacts);

        source[0] = 9;
        artifacts.Clear();
        Assert.True(MemoryMarshal.TryGetArray(artifact.Content, out var exposed));
        exposed.Array![exposed.Offset] = 8;

        Assert.Equal(1, Assert.Single(capture.Artifacts).Content.Span[0]);
        Assert.Equal("startup/run-value", artifact.Key);
        Assert.Equal("registry-value", artifact.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("two//segments")]
    [InlineData("Case/Alias")]
    public void Artifact_rejects_non_logical_or_path_like_keys(string key)
    {
        Assert.Throws<ArgumentException>(() =>
            BackupArtifact.Create(key, "registry-value", new byte[] { 1 }));
    }

    [Fact]
    public void Capture_rejects_duplicate_stable_keys()
    {
        var artifact = BackupArtifact.Create("same-key", "opaque", new byte[] { 1 });

        Assert.Throws<ArgumentException>(() =>
            BackupCapture.ForOperation(
                Fingerprint("source"),
                Fingerprint("source"),
                [artifact, artifact]));
    }

    [Theory]
    [InlineData("sha256", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("SHA-256", "short")]
    [InlineData("SHA-256", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("SHA-256", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEG")]
    public void Capture_rejects_noncanonical_source_fingerprints(string algorithm, string value)
    {
        var invalid = new StateFingerprint(algorithm, value);
        var artifact = BackupArtifact.Create("item", "opaque", [1]);

        Assert.Throws<ArgumentException>(() => BackupCapture.ForOperation(
            invalid,
            Fingerprint("live"),
            [artifact]));
        Assert.Throws<ArgumentException>(() => BackupCapture.ForOperation(
            Fingerprint("captured"),
            invalid,
            [artifact]));
    }

    [Theory]
    [InlineData("con")]
    [InlineData("a/com1")]
    public void Windows_reserved_names_are_valid_logical_keys_not_paths(string key)
    {
        var artifact = BackupArtifact.Create(key, "opaque", new byte[] { 1 });

        Assert.Equal(key, artifact.Key);
    }

    private static StateFingerprint Fingerprint(string value) =>
        new(
            "SHA-256",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value))));
}
