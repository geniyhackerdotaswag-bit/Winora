using System.Security.Cryptography;
using System.Text;
using Winora.Core.Contracts;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;

namespace Winora.Infrastructure.Backups;

internal sealed class WinoraStateSnapshotCapture
{
    private readonly WinoraDataPaths _paths;
    private readonly IValidatedFileAccess _validatedFileAccess;

    internal WinoraStateSnapshotCapture(
        WinoraDataPaths paths,
        IValidatedFileAccess? validatedFileAccess = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _validatedFileAccess = validatedFileAccess ?? new WindowsValidatedFileAccess();
    }

    internal BackupCapture Capture(CancellationToken cancellationToken)
    {
        var captured = ReadAllowedFiles(cancellationToken);
        var live = ReadAllowedFiles(cancellationToken);
        if (!StringComparer.Ordinal.Equals(ComputeDigest(captured), ComputeDigest(live)))
        {
            throw new BackupFingerprintMismatchException();
        }

        var digest = ComputeDigest(captured);
        var fingerprint = new Winora.Core.Changes.StateFingerprint("SHA-256", digest);
        return BackupCapture.ForWinoraState(fingerprint, fingerprint, captured);
    }

    private IReadOnlyList<BackupArtifact> ReadAllowedFiles(CancellationToken cancellationToken)
    {
        var artifacts = new List<BackupArtifact>();
        AddScope("data", _paths.DataDirectory, artifacts, cancellationToken);
        AddScope("assets", _paths.AssetsDirectory, artifacts, cancellationToken);
        return Array.AsReadOnly(
            artifacts.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray());
    }

    private void AddScope(
        string scopeName,
        string root,
        ICollection<BackupArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(root);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if ((rootAttributes & FileAttributes.Directory) == 0 ||
            (rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "A Winora state scope must be an ordinary non-reparse directory.");
        }

        AddPinnedDirectory(
            scopeName,
            root,
            root,
            artifacts,
            cancellationToken);
    }

    private void AddPinnedDirectory(
        string scopeName,
        string scopeRoot,
        string directory,
        ICollection<BackupArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        using var directoryLease = SecureOwnedPathLease.AcquireExistingDirectory(
            _paths,
            directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     directory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "A Winora state scope cannot contain reparse-point entries.");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                AddPinnedDirectory(
                    scopeName,
                    scopeRoot,
                    entry,
                    artifacts,
                    cancellationToken);
                continue;
            }

            using var file = _validatedFileAccess.OpenPinnedRead(
                entry,
                ValidatedFileUse.PublicRead);
            var content = file.ReadAllBytes(flushToDisk: false);
            var relative = Path.GetRelativePath(scopeRoot, entry).Replace('\\', '/');
            var backupPath = BackupArtifactPath.Normalize($"{scopeName}/{relative}");
            artifacts.Add(BackupArtifact.Create(
                backupPath,
                "winora-state-file",
                content));
        }
    }

    private static string ComputeDigest(IReadOnlyList<BackupArtifact> artifacts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var artifact in artifacts)
        {
            var path = Encoding.UTF8.GetBytes(artifact.Key);
            hash.AppendData(BitConverter.GetBytes(path.Length));
            hash.AppendData(path);
            hash.AppendData(BitConverter.GetBytes(artifact.Content.Length));
            hash.AppendData(artifact.Content.Span);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
