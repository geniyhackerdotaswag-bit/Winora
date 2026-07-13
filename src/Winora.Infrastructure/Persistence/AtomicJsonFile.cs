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
        JsonDocumentSerializer? serializer = null,
        TimeProvider? timeProvider = null)
        : this(paths, null, null, serializer, timeProvider)
    {
    }

    internal AtomicJsonFile(
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

    public async ValueTask<JsonDocumentEnvelope<TPayload>> CreateNewAsync<TPayload>(
        AuthoritativeJsonDestination destination,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        ValidateDestinationOwner(destination);
        using var prepared = PrepareAuthoritative(destination, payload, cancellationToken);
        return await ExecuteTransactionAsync(
            transaction => transaction.PublishNew(prepared),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<JsonDocumentEnvelope<TPayload>> WriteProjectionAsync<TPayload>(
        ProjectionJsonDestination destination,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        ValidateDestinationOwner(destination);
        using var prepared = PrepareProjection(destination, payload, cancellationToken);
        return await ExecuteTransactionAsync(
            transaction => transaction.ReplaceProjection(prepared),
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<JsonDocumentEnvelope<TPayload>> ReadAuthoritativeAsync<TPayload>(
        AuthoritativeJsonDestination destination,
        CancellationToken cancellationToken)
    {
        ValidateDestinationOwner(destination);
        cancellationToken.ThrowIfCancellationRequested();
        SecureOwnedPathLease.RejectReparseLeafIfExists(destination.FilePath);
        return ValueTask.FromResult(ReadExpectedDocument<TPayload>(
            destination.FilePath,
            destination.DocumentId));
    }

    public ValueTask<ProjectionJsonReadResult<TPayload>> ReadProjectionAsync<TPayload>(
        ProjectionJsonDestination destination,
        CancellationToken cancellationToken)
    {
        ValidateDestinationOwner(destination);
        cancellationToken.ThrowIfCancellationRequested();
        SecureOwnedPathLease.RejectReparseLeafIfExists(destination.FilePath);
        SecureOwnedPathLease.RejectReparseLeafIfExists(destination.LastKnownGoodFilePath);

        Exception? targetFailure = null;
        try
        {
            return ValueTask.FromResult(new ProjectionJsonReadResult<TPayload>(
                ReadExpectedDocument<TPayload>(destination.FilePath, destination.DocumentId),
                ProjectionReadSource.Primary));
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException)
        {
            targetFailure = exception;
        }

        try
        {
            return ValueTask.FromResult(new ProjectionJsonReadResult<TPayload>(
                ReadExpectedDocument<TPayload>(
                    destination.LastKnownGoodFilePath,
                    destination.DocumentId),
                ProjectionReadSource.LastKnownGood));
        }
        catch (FileNotFoundException) when (targetFailure is not null)
        {
            throw targetFailure;
        }
    }

    internal async ValueTask<JsonDocumentEnvelope<TPayload>> CreateNewAsync<TPayload>(
        string finalPath,
        string documentId,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        using var prepared = PrepareRaw(
            _paths.EnsureOwnedFilePath(finalPath),
            documentId,
            payload,
            isProjection: false,
            cancellationToken);
        return await ExecuteTransactionAsync(
            transaction => transaction.PublishNew(prepared),
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<JsonDocumentEnvelope<TPayload>> WriteProjectionAsync<TPayload>(
        string finalPath,
        string documentId,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        using var prepared = PrepareRaw(
            _paths.EnsureOwnedFilePath(finalPath),
            documentId,
            payload,
            isProjection: true,
            cancellationToken);
        return await ExecuteTransactionAsync(
            transaction => transaction.ReplaceProjection(prepared),
            cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<JsonDocumentEnvelope<TPayload>> ReadAsync<TPayload>(
        string finalPath,
        CancellationToken cancellationToken)
    {
        var ownedPath = _paths.EnsureOwnedFilePath(finalPath);
        return RunSerializedAsync(
            () => ReadWithRecovery<TPayload>(ownedPath, cancellationToken),
            cancellationToken);
    }

    internal PreparedJsonWrite<TPayload> PrepareAuthoritative<TPayload>(
        AuthoritativeJsonDestination destination,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        ValidateDestinationOwner(destination);
        return PrepareRaw(
            destination.FilePath,
            destination.DocumentId,
            payload,
            isProjection: false,
            cancellationToken);
    }

    internal PreparedJsonWrite<TPayload> PrepareProjection<TPayload>(
        ProjectionJsonDestination destination,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        ValidateDestinationOwner(destination);
        return PrepareRaw(
            destination.FilePath,
            destination.DocumentId,
            payload,
            isProjection: true,
            cancellationToken);
    }

    internal ValueTask<TResult> ExecuteTransactionAsync<TResult>(
        Func<AtomicJsonTransaction, TResult> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunSerializedAsync(
            () => action(new AtomicJsonTransaction(this, cancellationToken)),
            cancellationToken);
    }

    private PreparedJsonWrite<TPayload> PrepareRaw<TPayload>(
        string finalPath,
        string documentId,
        TPayload payload,
        bool isProjection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pathLease = SecureOwnedPathLease.Acquire(_paths, finalPath);
        string? temporaryPath = null;

        try
        {
            var directory = Path.GetDirectoryName(finalPath) ??
                throw new ArgumentException(
                    "The destination must have a parent directory.",
                    nameof(finalPath));
            var envelope = _serializer.CreateEnvelope(
                documentId,
                _timeProvider.GetUtcNow(),
                payload);
            var serialized = _serializer.Serialize(envelope);
            var expectedFileHash = SHA256.HashData(serialized);
            temporaryPath = Path.Combine(
                directory,
                $"{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
            WriteStagingFile(temporaryPath, serialized, cancellationToken);
            SecureOwnedPathLease.RejectReparseLeafIfExists(temporaryPath);
            var stagedBytes = _fileDurability.ReopenReadAndFlush(temporaryPath);
            ValidatePublishedBytes(stagedBytes, expectedFileHash, envelope);
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedJsonWrite<TPayload>(
                this,
                finalPath,
                isProjection,
                temporaryPath,
                expectedFileHash,
                envelope,
                pathLease);
        }
        catch
        {
            if (temporaryPath is not null)
            {
                File.Delete(temporaryPath);
            }

            pathLease.Dispose();
            throw;
        }
    }

    internal JsonDocumentEnvelope<TPayload> PublishPrepared<TPayload>(
        PreparedJsonWrite<TPayload> prepared,
        bool expectedProjection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(prepared.Owner, this) || prepared.IsProjection != expectedProjection)
        {
            throw new InvalidOperationException(
                "The prepared JSON write does not belong to this transaction or authority class.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        SecureOwnedPathLease.RejectReparseLeafIfExists(prepared.TemporaryPath);
        var finalPath = prepared.FinalPath;
        var lastKnownGoodPath = GetLastKnownGoodPath(finalPath);
        SecureOwnedPathLease.RejectReparseLeafIfExists(finalPath);
        if (expectedProjection)
        {
            SecureOwnedPathLease.RejectReparseLeafIfExists(lastKnownGoodPath);
        }

        var targetExists = File.Exists(finalPath);
        var targetIsValid = false;
        if (expectedProjection)
        {
            var current = TryRead<TPayload>(finalPath, out var targetEnvelope)
                ? targetEnvelope
                : TryRead<TPayload>(lastKnownGoodPath, out var recoveredEnvelope)
                    ? recoveredEnvelope
                    : null;
            targetIsValid = targetEnvelope is not null;
            if (current is not null &&
                !StringComparer.Ordinal.Equals(current.DocumentId, prepared.Envelope.DocumentId))
            {
                throw new InvalidDataException(
                    "A projection replacement cannot change the stable document identifier.");
            }
        }

        string? quarantinePath = null;
        if (expectedProjection && targetExists)
        {
            var backupPath = lastKnownGoodPath;
            if (!targetIsValid)
            {
                quarantinePath = $"{finalPath}.{Guid.NewGuid():N}.corrupt";
                backupPath = quarantinePath;
            }

            _publisher.ReplaceProjectionAsync(
                prepared.TemporaryPath,
                finalPath,
                backupPath,
                cancellationToken).GetAwaiter().GetResult();
        }
        else
        {
            _publisher.PublishNewAsync(
                prepared.TemporaryPath,
                finalPath,
                cancellationToken).GetAwaiter().GetResult();
        }

        var publishedBytes = _fileDurability.ReopenReadAndFlush(finalPath);
        ValidatePublishedBytes(
            publishedBytes,
            prepared.ExpectedFileHash,
            prepared.Envelope);
        cancellationToken.ThrowIfCancellationRequested();
        if (quarantinePath is not null)
        {
            File.Delete(quarantinePath);
        }

        return prepared.Envelope;
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

    private JsonDocumentEnvelope<TPayload> ReadExpectedDocument<TPayload>(
        string path,
        string expectedDocumentId)
    {
        var document = ReadDocument<TPayload>(path);
        if (!StringComparer.Ordinal.Equals(document.DocumentId, expectedDocumentId))
        {
            throw new InvalidDataException(
                "The persisted JSON document identity does not match its fixed-layout destination.");
        }

        return document;
    }

    private void ValidateDestinationOwner(JsonDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!ReferenceEquals(destination.Owner, _paths))
        {
            throw new ArgumentException(
                "The JSON destination was not issued by this persistence root.",
                nameof(destination));
        }
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

public enum ProjectionReadSource
{
    Primary = 0,
    LastKnownGood = 1,
}

public sealed record ProjectionJsonReadResult<TPayload>(
    JsonDocumentEnvelope<TPayload> Document,
    ProjectionReadSource Source);

internal sealed class PreparedJsonWrite<TPayload> : IDisposable
{
    private readonly SecureOwnedPathLease _pathLease;
    private bool _disposed;

    internal PreparedJsonWrite(
        AtomicJsonFile owner,
        string finalPath,
        bool isProjection,
        string temporaryPath,
        byte[] expectedFileHash,
        JsonDocumentEnvelope<TPayload> envelope,
        SecureOwnedPathLease pathLease)
    {
        Owner = owner;
        FinalPath = finalPath;
        IsProjection = isProjection;
        TemporaryPath = temporaryPath;
        ExpectedFileHash = expectedFileHash;
        Envelope = envelope;
        _pathLease = pathLease;
    }

    internal AtomicJsonFile Owner { get; }

    internal string FinalPath { get; }

    internal bool IsProjection { get; }

    internal string TemporaryPath { get; }

    internal byte[] ExpectedFileHash { get; }

    internal JsonDocumentEnvelope<TPayload> Envelope { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        File.Delete(TemporaryPath);
        _pathLease.Dispose();
        _disposed = true;
    }
}

internal sealed class AtomicJsonTransaction
{
    private readonly AtomicJsonFile _owner;
    private readonly CancellationToken _cancellationToken;

    internal AtomicJsonTransaction(
        AtomicJsonFile owner,
        CancellationToken cancellationToken)
    {
        _owner = owner;
        _cancellationToken = cancellationToken;
    }

    internal JsonDocumentEnvelope<TPayload> PublishNew<TPayload>(
        PreparedJsonWrite<TPayload> prepared) =>
        _owner.PublishPrepared(prepared, expectedProjection: false, _cancellationToken);

    internal JsonDocumentEnvelope<TPayload> ReplaceProjection<TPayload>(
        PreparedJsonWrite<TPayload> prepared) =>
        _owner.PublishPrepared(prepared, expectedProjection: true, _cancellationToken);

    internal JsonDocumentEnvelope<TPayload> ReadAuthoritative<TPayload>(
        AuthoritativeJsonDestination destination) =>
        _owner.ReadAuthoritativeAsync<TPayload>(destination, _cancellationToken)
            .GetAwaiter().GetResult();

    internal ProjectionJsonReadResult<TPayload> ReadProjection<TPayload>(
        ProjectionJsonDestination destination) =>
        _owner.ReadProjectionAsync<TPayload>(destination, _cancellationToken)
            .GetAwaiter().GetResult();
}
