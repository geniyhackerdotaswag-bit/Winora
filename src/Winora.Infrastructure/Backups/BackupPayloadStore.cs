using System.Security.Cryptography;
using Winora.Core.Contracts;
using Winora.Infrastructure.Persistence;

namespace Winora.Infrastructure.Backups;

internal sealed class BackupPayloadStore
{
    private readonly IFileDurability _durability;
    private readonly IValidatedFileAccess _validatedFileAccess;

    internal BackupPayloadStore(
        IFileDurability? durability = null,
        IValidatedFileAccess? validatedFileAccess = null)
    {
        _durability = durability ?? new WindowsFileDurability();
        _validatedFileAccess = validatedFileAccess ?? new WindowsValidatedFileAccess();
    }

    internal IReadOnlyList<BackupArtifactDocument> WriteAndVerify(
        string stagingDirectory,
        IReadOnlyList<BackupArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(artifacts);
        var payloadDirectory = Path.Combine(stagingDirectory, "payload");
        if (!Directory.Exists(payloadDirectory))
        {
            throw new InvalidDataException(
                "The protected backup payload directory was not prepared.");
        }

        using var payloadLease = SecureBackupDirectoryLayout.AcquirePinnedDirectory(
            payloadDirectory,
            allowRename: false);
        var documents = new List<BackupArtifactDocument>(artifacts.Count);
        var logicalKeys = new HashSet<string>(StringComparer.Ordinal);
        var storageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var storageFileName = BackupStorageName.ForLogicalKey(artifact.Key);
            if (!logicalKeys.Add(artifact.Key) || !storageNames.Add(storageFileName))
            {
                throw new InvalidDataException("A backup capture contains duplicate artifact identities.");
            }

            var artifactPath = Path.Combine(payloadDirectory, storageFileName);
            using (var stream = new FileStream(
                       artifactPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan))
            {
                stream.Write(artifact.Content.Span);
                stream.Flush();
                _durability.FlushToDisk(stream);
            }

            using var staged = _validatedFileAccess.Open(
                artifactPath,
                FileAccess.ReadWrite,
                ValidatedFileUse.StagingReadback);
            var readback = staged.ReadAllBytes(flushToDisk: true);
            if (!readback.AsSpan().SequenceEqual(artifact.Content.Span))
            {
                throw new InvalidDataException("A backup artifact changed during durable staging.");
            }

            documents.Add(new BackupArtifactDocument(
                artifact.Key,
                storageFileName,
                artifact.Type,
                readback.LongLength,
                Convert.ToHexString(SHA256.HashData(readback))));
        }

        if (!payloadLease.MatchesPath(payloadDirectory))
        {
            throw new IOException(
                "The protected backup payload directory changed during staging.");
        }

        return Array.AsReadOnly(documents.ToArray());
    }

    internal IReadOnlyList<BackupArtifact> ReadAndVerify(
        string backupDirectory,
        BackupManifestDocument manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        var payloadDirectory = Path.Combine(backupDirectory, "payload");
        using var payloadLease = SecureBackupDirectoryLayout.AcquirePinnedDirectory(
            payloadDirectory,
            allowRename: false);
        var artifacts = new List<BackupArtifact>(manifest.Artifacts.Count);
        foreach (var document in manifest.Artifacts)
        {
            BackupStorageName.Validate(document);
            var artifactPath = Path.Combine(payloadDirectory, document.StorageFileName);
            byte[] bytes;
            try
            {
                using var artifact = _validatedFileAccess.OpenPinnedRead(
                    artifactPath,
                    ValidatedFileUse.PublicRead);
                bytes = artifact.ReadAllBytes(flushToDisk: false);
            }
            catch (FileNotFoundException exception)
            {
                throw new InvalidDataException(
                    "A committed backup artifact is missing.",
                    exception);
            }
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (bytes.LongLength != document.Length ||
                !StringComparer.Ordinal.Equals(hash, document.Sha256))
            {
                throw new InvalidDataException("A committed backup artifact failed hash verification.");
            }

            artifacts.Add(BackupArtifact.Create(document.LogicalKey, document.Type, bytes));
        }

        if (!payloadLease.MatchesPath(payloadDirectory))
        {
            throw new IOException(
                "The committed backup payload directory changed during verification.");
        }

        return Array.AsReadOnly(artifacts.ToArray());
    }
}
