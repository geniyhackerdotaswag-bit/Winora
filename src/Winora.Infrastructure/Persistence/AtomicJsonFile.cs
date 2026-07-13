using System.Collections.Concurrent;
using System.Security.Cryptography;
using Winora.Infrastructure.Paths;

namespace Winora.Infrastructure.Persistence;

public sealed class AtomicJsonFile
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LocalLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly WinoraDataPaths _paths;
    private readonly IWriteThroughPublisher _publisher;
    private readonly IFileDurability _fileDurability;
    private readonly JsonDocumentSerializer _serializer;
    private readonly TimeProvider _timeProvider;

    public AtomicJsonFile(
        WinoraDataPaths paths,
        IWriteThroughPublisher? publisher = null,
        IFileDurability? fileDurability = null,
        JsonDocumentSerializer? serializer = null,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _fileDurability = fileDurability ?? new WindowsFileDurability();
        _publisher = publisher ?? new WriteThroughPublisher(
            new WindowsAtomicFileOperations(),
            _fileDurability);
        _serializer = serializer ?? new JsonDocumentSerializer();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<JsonDocumentEnvelope<TPayload>> CreateNewAsync<TPayload>(
        string finalPath,
        string documentId,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        var ownedPath = _paths.EnsureOwnedFilePath(finalPath);
        return RunSerializedAsync(
            () => WriteCore(ownedPath, documentId, payload, isProjection: false, cancellationToken),
            cancellationToken);
    }

    public ValueTask<JsonDocumentEnvelope<TPayload>> WriteProjectionAsync<TPayload>(
        string finalPath,
        string documentId,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        var ownedPath = _paths.EnsureOwnedFilePath(finalPath);
        return RunSerializedAsync(
            () => WriteCore(ownedPath, documentId, payload, isProjection: true, cancellationToken),
            cancellationToken);
    }

    public ValueTask<JsonDocumentEnvelope<TPayload>> ReadAsync<TPayload>(
        string finalPath,
        CancellationToken cancellationToken)
    {
        var ownedPath = _paths.EnsureOwnedFilePath(finalPath);
        return RunSerializedAsync(
            () => ReadWithRecovery<TPayload>(ownedPath, cancellationToken),
            cancellationToken);
    }

    private JsonDocumentEnvelope<TPayload> WriteCore<TPayload>(
        string finalPath,
        string documentId,
        TPayload payload,
        bool isProjection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(finalPath) ??
            throw new ArgumentException("The destination must have a parent directory.", nameof(finalPath));
        Directory.CreateDirectory(directory);

        var lastKnownGoodPath = GetLastKnownGoodPath(finalPath);
        var targetExists = File.Exists(finalPath);
        var targetIsValid = false;
        if (isProjection)
        {
            var current = TryRead<TPayload>(finalPath, out var targetEnvelope)
                ? targetEnvelope
                : TryRead<TPayload>(lastKnownGoodPath, out var recoveredEnvelope)
                    ? recoveredEnvelope
                    : null;
            targetIsValid = targetEnvelope is not null;
            if (current is not null &&
                !StringComparer.Ordinal.Equals(current.DocumentId, documentId))
            {
                throw new InvalidDataException(
                    "A projection replacement cannot change the stable document identifier.");
            }
        }

        var envelope = _serializer.CreateEnvelope(
            documentId,
            _timeProvider.GetUtcNow(),
            payload);
        var serialized = _serializer.Serialize(envelope);
        var expectedFileHash = SHA256.HashData(serialized);
        var temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
        string? quarantinePath = null;

        try
        {
            WriteStagingFile(temporaryPath, serialized, cancellationToken);
            var stagedBytes = _fileDurability.ReopenReadAndFlush(temporaryPath);
            ValidatePublishedBytes(stagedBytes, expectedFileHash, envelope);
            cancellationToken.ThrowIfCancellationRequested();

            if (isProjection && targetExists)
            {
                var backupPath = lastKnownGoodPath;
                if (!targetIsValid)
                {
                    quarantinePath = $"{finalPath}.{Guid.NewGuid():N}.corrupt";
                    backupPath = quarantinePath;
                }

                _publisher.ReplaceProjectionAsync(
                    temporaryPath,
                    finalPath,
                    backupPath,
                    cancellationToken).GetAwaiter().GetResult();
            }
            else
            {
                _publisher.PublishNewAsync(
                    temporaryPath,
                    finalPath,
                    cancellationToken).GetAwaiter().GetResult();
            }

            var publishedBytes = _fileDurability.ReopenReadAndFlush(finalPath);
            ValidatePublishedBytes(publishedBytes, expectedFileHash, envelope);
            cancellationToken.ThrowIfCancellationRequested();

            if (quarantinePath is not null)
            {
                File.Delete(quarantinePath);
            }

            return envelope;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private void WriteStagingFile(
        string temporaryPath,
        ReadOnlySpan<byte> serialized,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        stream.Write(serialized);
        stream.Flush();
        _fileDurability.FlushToDisk(stream);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private JsonDocumentEnvelope<TPayload> ReadWithRecovery<TPayload>(
        string finalPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Exception? targetFailure = null;
        try
        {
            return ReadDocument<TPayload>(finalPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException)
        {
            targetFailure = exception;
        }

        try
        {
            return ReadDocument<TPayload>(GetLastKnownGoodPath(finalPath));
        }
        catch (FileNotFoundException) when (targetFailure is not null)
        {
            throw targetFailure;
        }
    }

    private bool TryRead<TPayload>(
        string path,
        out JsonDocumentEnvelope<TPayload>? envelope)
    {
        try
        {
            envelope = ReadDocument<TPayload>(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException)
        {
            envelope = null;
            return false;
        }
    }

    private JsonDocumentEnvelope<TPayload> ReadDocument<TPayload>(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return _serializer.DeserializeAndValidate<TPayload>(bytes);
    }

    private void ValidatePublishedBytes<TPayload>(
        byte[] bytes,
        ReadOnlySpan<byte> expectedFileHash,
        JsonDocumentEnvelope<TPayload> expectedEnvelope)
    {
        var actualFileHash = SHA256.HashData(bytes);
        if (!CryptographicOperations.FixedTimeEquals(expectedFileHash, actualFileHash))
        {
            throw new InvalidDataException("The JSON file changed during durable publication.");
        }

        var actualEnvelope = _serializer.DeserializeAndValidate<TPayload>(bytes);
        if (actualEnvelope.SchemaVersion != expectedEnvelope.SchemaVersion ||
            actualEnvelope.CreatedUtc != expectedEnvelope.CreatedUtc ||
            !StringComparer.Ordinal.Equals(actualEnvelope.DocumentId, expectedEnvelope.DocumentId) ||
            !StringComparer.Ordinal.Equals(
                actualEnvelope.PayloadSha256,
                expectedEnvelope.PayloadSha256))
        {
            throw new InvalidDataException(
                "The published JSON envelope does not match the staged document.");
        }
    }

    private async ValueTask<T> RunSerializedAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        var localLock = LocalLocks.GetOrAdd(_paths.RootDirectory, static _ => new SemaphoreSlim(1, 1));
        await localLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => GlobalPersistenceMutex.Shared.Execute(action, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            localLock.Release();
        }
    }

    private static string GetLastKnownGoodPath(string finalPath) =>
        $"{finalPath}.last-known-good";
}
