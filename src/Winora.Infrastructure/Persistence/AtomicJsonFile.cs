using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
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
    private readonly IValidatedFileAccess _validatedFileAccess;
    private readonly IAtomicFileCleanup _cleanup;
    private readonly IAtomicPublicationRaceHook? _publicationRaceHook;

    public AtomicJsonFile(
        WinoraDataPaths paths,
        JsonDocumentSerializer? serializer = null,
        TimeProvider? timeProvider = null)
        : this(paths, null, null, serializer, timeProvider, null, null, null)
    {
    }

    internal AtomicJsonFile(
        WinoraDataPaths paths,
        IWriteThroughPublisher? publisher = null,
        IFileDurability? fileDurability = null,
        JsonDocumentSerializer? serializer = null,
        TimeProvider? timeProvider = null,
        IValidatedFileObserver? validatedFileObserver = null,
        IAtomicPublicationRaceHook? publicationRaceHook = null,
        IAtomicFileCleanup? cleanup = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _fileDurability = fileDurability ?? new WindowsFileDurability();
        _validatedFileAccess = new WindowsValidatedFileAccess(validatedFileObserver);
        _publisher = publisher ?? new WriteThroughPublisher(
            new WindowsAtomicFileOperations(),
            _validatedFileAccess);
        _serializer = serializer ?? new JsonDocumentSerializer();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _publicationRaceHook = publicationRaceHook;
        _cleanup = cleanup ?? new AtomicFileCleanup();
    }

    public async ValueTask<JsonDocumentEnvelope<TPayload>> CreateNewAsync<TPayload>(
        AuthoritativeJsonDestination destination,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        ValidateDestinationOwner(destination);
        var prepared = PrepareAuthoritative(destination, payload, cancellationToken);
        return await ExecutePreparedAsync(
            prepared,
            transaction => transaction.PublishNew(prepared),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<JsonDocumentEnvelope<TPayload>> WriteProjectionAsync<TPayload>(
        ProjectionJsonDestination destination,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        ValidateDestinationOwner(destination);
        var prepared = PrepareProjection(destination, payload, cancellationToken);
        return await ExecutePreparedAsync(
            prepared,
            transaction => transaction.ReplaceProjection(prepared),
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<JsonDocumentEnvelope<TPayload>> ReadAuthoritativeAsync<TPayload>(
        AuthoritativeJsonDestination destination,
        CancellationToken cancellationToken)
    {
        ValidateDestinationOwner(destination);
        cancellationToken.ThrowIfCancellationRequested();
        using var pathLease = SecureOwnedPathLease.Acquire(_paths, destination.FilePath);
        return ValueTask.FromResult(ReadExpectedDocument<TPayload>(
            destination.FilePath,
            destination.DocumentId,
            ValidatedFileUse.PublicRead));
    }

    public ValueTask<ProjectionJsonReadResult<TPayload>> ReadProjectionAsync<TPayload>(
        ProjectionJsonDestination destination,
        CancellationToken cancellationToken)
    {
        ValidateDestinationOwner(destination);
        cancellationToken.ThrowIfCancellationRequested();
        using var pathLease = SecureOwnedPathLease.Acquire(_paths, destination.FilePath);

        Exception? targetFailure = null;
        try
        {
            return ValueTask.FromResult(new ProjectionJsonReadResult<TPayload>(
                ReadExpectedDocument<TPayload>(
                    destination.FilePath,
                    destination.DocumentId,
                    ValidatedFileUse.PublicRead),
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
                    destination.DocumentId,
                    ValidatedFileUse.PublicRead),
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
        var prepared = PrepareRaw(
            _paths.EnsureOwnedFilePath(finalPath),
            documentId,
            payload,
            isProjection: false,
            cancellationToken);
        return await ExecutePreparedAsync(
            prepared,
            transaction => transaction.PublishNew(prepared),
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<JsonDocumentEnvelope<TPayload>> WriteProjectionAsync<TPayload>(
        string finalPath,
        string documentId,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        var prepared = PrepareRaw(
            _paths.EnsureOwnedFilePath(finalPath),
            documentId,
            payload,
            isProjection: true,
            cancellationToken);
        return await ExecutePreparedAsync(
            prepared,
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

    private async ValueTask<TResult> ExecutePreparedAsync<TPayload, TResult>(
        PreparedJsonWrite<TPayload> prepared,
        Func<AtomicJsonTransaction, TResult> action,
        CancellationToken cancellationToken)
    {
        Exception? primaryFailure = null;
        TResult? result = default;
        try
        {
            result = await ExecuteTransactionAsync(action, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        Exception? cleanupFailure = null;
        try
        {
            prepared.Dispose();
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        if (primaryFailure is not null && cleanupFailure is not null)
        {
            throw new AggregateException(
                "Atomic JSON publication failed and its temporary-file cleanup also failed.",
                primaryFailure,
                cleanupFailure);
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        return result!;
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
        ValidatedFileHandle? stagingHandle = null;

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
            stagingHandle = _validatedFileAccess.Open(
                temporaryPath,
                FileAccess.ReadWrite,
                ValidatedFileUse.StagingReadback);
            var stagedBytes = stagingHandle.ReadAllBytes(flushToDisk: true);
            ValidatePublishedBytes(stagedBytes, expectedFileHash, envelope);
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedJsonWrite<TPayload>(
                this,
                finalPath,
                isProjection,
                temporaryPath,
                expectedFileHash,
                envelope,
                pathLease,
                stagingHandle,
                _cleanup);
        }
        catch (Exception primaryFailure)
        {
            Exception? cleanupFailure = null;
            try
            {
                stagingHandle?.Dispose();
                if (temporaryPath is not null)
                {
                    _cleanup.Delete(temporaryPath);
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
            finally
            {
                pathLease.Dispose();
            }

            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "Atomic JSON preparation failed and its temporary-file cleanup also failed.",
                    primaryFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
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
        var stagingIdentity = prepared.ReleaseStagingHandle();
        var finalPath = prepared.FinalPath;
        var lastKnownGoodPath = GetLastKnownGoodPath(finalPath);
        var target = ProbeDocument<TPayload>(finalPath, ValidatedFileUse.ProjectionProbe);
        var lastKnownGood = expectedProjection
            ? ProbeDocument<TPayload>(lastKnownGoodPath, ValidatedFileUse.ProjectionProbe)
            : FileProbe<TPayload>.Missing;
        if (expectedProjection)
        {
            var current = target.Document ?? lastKnownGood.Document;
            if (current is not null &&
                !StringComparer.Ordinal.Equals(current.DocumentId, prepared.Envelope.DocumentId))
            {
                throw new InvalidDataException(
                    "A projection replacement cannot change the stable document identifier.");
            }
        }

        string? quarantinePath = null;
        string? backupPath = null;
        if (expectedProjection && target.Exists)
        {
            backupPath = lastKnownGoodPath;
            if (target.Document is null)
            {
                quarantinePath = $"{finalPath}.{Guid.NewGuid():N}.corrupt";
                backupPath = quarantinePath;
            }
        }

        EnsureIdentityUnchanged(
            prepared.TemporaryPath,
            stagingIdentity,
            "The prepared staging file identity changed before publication.");
        _publicationRaceHook?.AfterInitialIdentityValidation(new AtomicPublicationContext(
            prepared.TemporaryPath,
            finalPath,
            backupPath));
        EnsureIdentityUnchanged(
            prepared.TemporaryPath,
            stagingIdentity,
            "The prepared staging file identity changed during publication preflight.");
        EnsureIdentityUnchanged(
            finalPath,
            target.Identity,
            "The target identity changed during publication preflight.");
        if (expectedProjection)
        {
            EnsureIdentityUnchanged(
                lastKnownGoodPath,
                lastKnownGood.Identity,
                "The last-known-good identity changed during publication preflight.");
        }

        if (backupPath is not null)
        {
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

        using var published = _validatedFileAccess.Open(
            finalPath,
            FileAccess.ReadWrite,
            ValidatedFileUse.PostPublication);
        if (published.Identity != stagingIdentity)
        {
            throw new InvalidDataException(
                "The published file identity does not match the validated staging object.");
        }

        var publishedBytes = published.ReadAllBytes(flushToDisk: true);
        ValidatePublishedBytes(
            publishedBytes,
            prepared.ExpectedFileHash,
            prepared.Envelope);
        if (backupPath is not null)
        {
            using var backup = _validatedFileAccess.Open(
                backupPath,
                FileAccess.Read,
                ValidatedFileUse.PostPublication);
            if (backup.Identity != target.Identity)
            {
                throw new InvalidDataException(
                    "The retained backup identity does not match the replaced target object.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (quarantinePath is not null)
        {
            _cleanup.Delete(quarantinePath);
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
        using var pathLease = SecureOwnedPathLease.Acquire(_paths, finalPath);

        Exception? targetFailure = null;
        try
        {
            return ReadDocument<TPayload>(finalPath, ValidatedFileUse.PublicRead);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException)
        {
            targetFailure = exception;
        }

        try
        {
            return ReadDocument<TPayload>(
                GetLastKnownGoodPath(finalPath),
                ValidatedFileUse.PublicRead);
        }
        catch (FileNotFoundException) when (targetFailure is not null)
        {
            throw targetFailure;
        }
    }

    private FileProbe<TPayload> ProbeDocument<TPayload>(
        string path,
        ValidatedFileUse use)
    {
        using var file = _validatedFileAccess.TryOpen(path, FileAccess.Read, use);
        if (file is null)
        {
            return FileProbe<TPayload>.Missing;
        }

        JsonDocumentEnvelope<TPayload>? document;
        try
        {
            document = _serializer.DeserializeAndValidate<TPayload>(
                file.ReadAllBytes(flushToDisk: false));
        }
        catch (InvalidDataException)
        {
            document = null;
        }

        return new FileProbe<TPayload>(file.Identity, document);
    }

    private JsonDocumentEnvelope<TPayload> ReadDocument<TPayload>(
        string path,
        ValidatedFileUse use)
    {
        using var file = _validatedFileAccess.Open(path, FileAccess.Read, use);
        return _serializer.DeserializeAndValidate<TPayload>(
            file.ReadAllBytes(flushToDisk: false));
    }

    private JsonDocumentEnvelope<TPayload> ReadExpectedDocument<TPayload>(
        string path,
        string expectedDocumentId,
        ValidatedFileUse use)
    {
        var document = ReadDocument<TPayload>(path, use);
        if (!StringComparer.Ordinal.Equals(document.DocumentId, expectedDocumentId))
        {
            throw new InvalidDataException(
                "The persisted JSON document identity does not match its fixed-layout destination.");
        }

        return document;
    }

    private void EnsureIdentityUnchanged(
        string path,
        ValidatedFileIdentity? expectedIdentity,
        string message)
    {
        ValidatedFileIdentity? actualIdentity;
        try
        {
            using var file = _validatedFileAccess.TryOpen(
                path,
                FileAccess.Read,
                ValidatedFileUse.PrePublication);
            actualIdentity = file?.Identity;
        }
        catch (IOException exception)
        {
            throw new InvalidDataException(message, exception);
        }

        if (actualIdentity != expectedIdentity)
        {
            throw new InvalidDataException(message);
        }
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

    private readonly record struct FileProbe<TPayload>(
        ValidatedFileIdentity? Identity,
        JsonDocumentEnvelope<TPayload>? Document)
    {
        internal static FileProbe<TPayload> Missing => new(null, null);

        internal bool Exists => Identity.HasValue;
    }
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
    private readonly IAtomicFileCleanup _cleanup;
    private ValidatedFileHandle? _stagingHandle;
    private bool _disposed;

    internal PreparedJsonWrite(
        AtomicJsonFile owner,
        string finalPath,
        bool isProjection,
        string temporaryPath,
        byte[] expectedFileHash,
        JsonDocumentEnvelope<TPayload> envelope,
        SecureOwnedPathLease pathLease,
        ValidatedFileHandle stagingHandle,
        IAtomicFileCleanup cleanup)
    {
        Owner = owner;
        FinalPath = finalPath;
        IsProjection = isProjection;
        TemporaryPath = temporaryPath;
        ExpectedFileHash = expectedFileHash;
        Envelope = envelope;
        _pathLease = pathLease;
        _stagingHandle = stagingHandle;
        _cleanup = cleanup;
    }

    internal AtomicJsonFile Owner { get; }

    internal string FinalPath { get; }

    internal bool IsProjection { get; }

    internal string TemporaryPath { get; }

    internal byte[] ExpectedFileHash { get; }

    internal JsonDocumentEnvelope<TPayload> Envelope { get; }

    internal ValidatedFileIdentity ReleaseStagingHandle()
    {
        var stagingHandle = _stagingHandle ??
            throw new InvalidOperationException("The validated staging handle was already released.");
        var identity = stagingHandle.Identity;
        stagingHandle.Dispose();
        _stagingHandle = null;
        return identity;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _stagingHandle?.Dispose();
            _stagingHandle = null;
            _cleanup.Delete(TemporaryPath);
        }
        finally
        {
            _pathLease.Dispose();
            _disposed = true;
        }
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
