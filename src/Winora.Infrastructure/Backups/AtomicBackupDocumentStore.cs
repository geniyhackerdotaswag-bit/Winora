using System.Security.Cryptography;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;

namespace Winora.Infrastructure.Backups;

internal sealed class AtomicBackupDocumentStore : IBackupDocumentStore
{
    private readonly WinoraDataPaths _paths;
    private readonly AtomicJsonFile _documents;
    private readonly JsonDocumentSerializer _serializer;
    private readonly IValidatedFileAccess _validatedFileAccess;
    private readonly TimeProvider _timeProvider;

    internal AtomicBackupDocumentStore(
        WinoraDataPaths paths,
        TimeProvider? timeProvider = null,
        IValidatedFileAccess? validatedFileAccess = null,
        IValidatedFileObserver? validatedFileObserver = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _serializer = new JsonDocumentSerializer();
        _documents = new AtomicJsonFile(
            paths,
            publisher: null,
            fileDurability: null,
            _serializer,
            _timeProvider,
            validatedFileObserver);
        _validatedFileAccess = validatedFileAccess ??
            new WindowsValidatedFileAccess(validatedFileObserver);
    }

    public async ValueTask WriteStagingManifestAsync(
        string backupId,
        BackupManifestDocument manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!StringComparer.Ordinal.Equals(backupId, manifest.BackupId))
        {
            throw new InvalidDataException(
                "A staging manifest is not bound to its fixed backup destination.");
        }

        await _documents.CreateNewAsync(
            _paths.GetBackupStagingManifestDocument(backupId),
            manifest,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PublishCommittedMarkerAsync(
        string backupId,
        string backupDigest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDigest);
        var manifestBytes = ReadFinalManifestBytes(backupId);
        var envelope = _serializer.DeserializeAndValidate<BackupManifestDocument>(manifestBytes);
        ValidateEnvelope(backupId, backupDigest, envelope);
        var marker = new BackupCommittedMarkerDocument(
            backupId,
            Convert.ToHexString(SHA256.HashData(manifestBytes)),
            backupDigest,
            GetUtcNow());
        await _documents.CreateNewAsync(
            _paths.GetBackupCommittedManifestDocument(backupId),
            marker,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CommittedBackupManifest> ReadCommittedManifestAsync(
        string backupId,
        CancellationToken cancellationToken)
    {
        var backupDirectory = _paths.GetBackupDirectory(backupId);
        if (!Directory.Exists(backupDirectory))
        {
            throw new InvalidDataException("The committed backup directory is missing.");
        }

        using var directoryLease = SecureOwnedPathLease.AcquireExistingDirectory(
            _paths,
            backupDirectory);
        JsonDocumentEnvelope<BackupCommittedMarkerDocument> markerEnvelope;
        try
        {
            markerEnvelope = await _documents.ReadAuthoritativeAsync<BackupCommittedMarkerDocument>(
                _paths.GetBackupCommittedManifestDocument(backupId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidDataException(
                "A backup without its committed marker is not usable.",
                exception);
        }
        var marker = markerEnvelope.Payload;
        if (!StringComparer.Ordinal.Equals(marker.BackupId, backupId) ||
            !IsUpperHexSha256(marker.ManifestSha256) ||
            !IsUpperHexSha256(marker.BackupDigest) ||
            marker.CommittedUtc == default ||
            marker.CommittedUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("The committed backup marker is invalid.");
        }

        var manifestBytes = ReadFinalManifestBytes(backupId);
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(marker.ManifestSha256),
                Convert.FromHexString(manifestHash)))
        {
            throw new InvalidDataException(
                "The committed backup manifest does not match its durable marker.");
        }

        var manifestEnvelope = _serializer.DeserializeAndValidate<BackupManifestDocument>(
            manifestBytes);
        ValidateEnvelope(backupId, marker.BackupDigest, manifestEnvelope);
        return new CommittedBackupManifest(
            manifestEnvelope.Payload,
            marker.CommittedUtc);
    }

    private byte[] ReadFinalManifestBytes(string backupId)
    {
        var backupDirectory = _paths.GetBackupDirectory(backupId);
        using var directoryLease = SecureOwnedPathLease.AcquireExistingDirectory(
            _paths,
            backupDirectory);
        var manifestPath = Path.Combine(backupDirectory, "manifest.json");
        try
        {
            using var manifest = _validatedFileAccess.OpenPinnedRead(
                manifestPath,
                ValidatedFileUse.PublicRead);
            return manifest.ReadAllBytes(flushToDisk: false);
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidDataException(
                "The committed backup manifest is missing.",
                exception);
        }
    }

    private static void ValidateEnvelope(
        string backupId,
        string backupDigest,
        JsonDocumentEnvelope<BackupManifestDocument> envelope)
    {
        if (!StringComparer.Ordinal.Equals(envelope.DocumentId, backupId) ||
            !StringComparer.Ordinal.Equals(envelope.Payload.BackupId, backupId) ||
            !StringComparer.Ordinal.Equals(envelope.Payload.BackupDigest, backupDigest))
        {
            throw new InvalidDataException(
                "The backup envelope, marker, and manifest identities do not agree.");
        }

        envelope.Payload.Validate();
    }

    private static bool IsUpperHexSha256(string? value) =>
        value is not null &&
        value.Length == 64 &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'A' and <= 'F'));

    private DateTimeOffset GetUtcNow()
    {
        var now = _timeProvider.GetUtcNow();
        if (now == default || now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "The backup committed-marker clock must return a non-default UTC timestamp.");
        }

        return now;
    }
}
