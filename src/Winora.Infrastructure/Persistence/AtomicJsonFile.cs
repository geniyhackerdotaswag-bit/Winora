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
            new WindowsAtomicFileOperations());
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
        return RunSerializedAsync(
            () => ReadAuthoritativeCore<TPayload>(destination, cancellationToken),
            cancellationToken);
    }

    public ValueTask<ProjectionJsonReadResult<TPayload>> ReadProjectionAsync<TPayload>(
        ProjectionJsonDestination destination,
        CancellationToken cancellationToken)
    {
        return RunSerializedAsync(
            () => ReadProjectionCore<TPayload>(destination, cancellationToken),
            cancellationToken);
    }

    internal JsonDocumentEnvelope<TPayload> ReadAuthoritativeCore<TPayload>(
        AuthoritativeJsonDestination destination,
        CancellationToken cancellationToken)
    {
        ValidateDestinationOwner(destination);
        cancellationToken.ThrowIfCancellationRequested();
        using var pathLease = SecureOwnedPathLease.Acquire(_paths, destination.FilePath);
        return ReadExpectedDocument<TPayload>(
            destination.FilePath,
            destination.DocumentId,
            ValidatedFileUse.PublicRead);
    }

    internal ProjectionJsonReadResult<TPayload> ReadProjectionCore<TPayload>(
        ProjectionJsonDestination destination,
        CancellationToken cancellationToken)
    {
        ValidateDestinationOwner(destination);
        cancellationToken.ThrowIfCancellationRequested();
        using var pathLease = SecureOwnedPathLease.Acquire(_paths, destination.FilePath);

        Exception? targetFailure = null;
        try
        {
            return new ProjectionJsonReadResult<TPayload>(
                ReadExpectedDocument<TPayload>(
                    destination.FilePath,
                    destination.DocumentId,
                    ValidatedFileUse.PublicRead),
                ProjectionReadSource.Primary);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException)
        {
            targetFailure = exception;
        }

        try
        {
            return new ProjectionJsonReadResult<TPayload>(
                ReadExpectedDocument<TPayload>(
                    destination.LastKnownGoodFilePath,
                    destination.DocumentId,
                    ValidatedFileUse.PublicRead),
                ProjectionReadSource.LastKnownGood);
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
        var disposalOwnershipTransferred = 0;
        try
        {
            return await RunSerializedAsync(
                () =>
                {
                    Interlocked.Exchange(ref disposalOwnershipTransferred, 1);
                    return ExecutePreparedWithinSerialization(
                        prepared,
                        action,
                        cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception primaryFailure)
        {
            if (Volatile.Read(ref disposalOwnershipTransferred) == 0)
            {
                try
                {
                    prepared.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(
                        "Atomic JSON serialization was not entered and temporary-file cleanup also failed.",
                        primaryFailure,
                        cleanupFailure);
                }
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }
    }

    private TResult ExecutePreparedWithinSerialization<TPayload, TResult>(
        PreparedJsonWrite<TPayload> prepared,
        Func<AtomicJsonTransaction, TResult> action,
        CancellationToken cancellationToken)
    {
        Exception? primaryFailure = null;
        TResult? result = default;
        try
        {
            result = action(new AtomicJsonTransaction(this, cancellationToken));
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
            stagingHandle = _validatedFileAccess.OpenForMutation(
                temporaryPath,
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
                if (stagingHandle is not null)
                {
                    _cleanup.Delete(stagingHandle);
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
            finally
            {
                stagingHandle?.Dispose();
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
        var stagingHandle = prepared.BeginPublication();
        var stagingIdentity = stagingHandle.Identity;
        var finalPath = prepared.FinalPath;
        var lastKnownGoodPath = GetLastKnownGoodPath(finalPath);
        using var target = ProbeDocument<TPayload>(finalPath, ValidatedFileUse.ProjectionProbe);
        using var lastKnownGood = expectedProjection
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

        RevalidatePinnedHandle(
            stagingHandle,
            stagingIdentity,
            "The prepared staging file identity changed before publication.");
        RevalidatePinnedProbe(
            target,
            "The target identity changed before publication.");
        RevalidatePinnedProbe(
            lastKnownGood,
            "The last-known-good identity changed before publication.");
        try
        {
            _publicationRaceHook?.AfterInitialIdentityValidation(new AtomicPublicationContext(
                prepared.TemporaryPath,
                finalPath,
                backupPath));
        }
        catch (IOException exception)
        {
            throw new InvalidDataException(
                "A pinned publication leaf resisted an identity swap during preflight.",
                exception);
        }

        RevalidatePinnedHandle(
            stagingHandle,
            stagingIdentity,
            "The prepared staging file identity changed during publication preflight.");
        RevalidatePinnedProbe(
            target,
            "The target identity changed during publication preflight.");
        RevalidatePinnedProbe(
            lastKnownGood,
            "The last-known-good identity changed during publication preflight.");

        if (backupPath is not null)
        {
            _publisher.ReplaceProjectionAsync(
                stagingHandle,
                target.Handle!,
                finalPath,
                StringComparer.OrdinalIgnoreCase.Equals(backupPath, lastKnownGoodPath)
                    ? lastKnownGood.Handle
                    : null,
                backupPath,
                prepared.ExpectedFileHash,
                cancellationToken).GetAwaiter().GetResult();
        }
        else
        {
            _publisher.PublishNewAsync(
                stagingHandle,
                finalPath,
                prepared.ExpectedFileHash,
                cancellationToken).GetAwaiter().GetResult();
        }

        RevalidatePinnedHandle(
            stagingHandle,
            stagingIdentity,
            "The published file identity does not match the validated staging object.");
        var publishedBytes = stagingHandle.ReadAllBytes(flushToDisk: true);
        ValidatePublishedBytes(
            publishedBytes,
            prepared.ExpectedFileHash,
            prepared.Envelope);
        if (backupPath is not null)
        {
            RevalidatePinnedHandle(
                target.Handle!,
                target.Identity!.Value,
                "The retained backup identity does not match the replaced target object.");
        }

        if (quarantinePath is not null)
        {
            try
            {
                _cleanup.Delete(target.Handle!);
            }
            catch (IOException)
            {
                // The new projection is already durable. A retained corrupt quarantine
                // is safe and can be removed by maintenance; it must not turn the
                // committed publication into a reported failure.
            }
        }

        return prepared.Envelope;
    }

    internal void BeforePreparedHandleRelease() =>
        _publicationRaceHook?.BeforePreparedHandleRelease();

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
        var file = _validatedFileAccess.TryOpenForMutation(path, use);
        if (file is null)
        {
            return FileProbe<TPayload>.Missing;
        }

        try
        {
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

            return new FileProbe<TPayload>(file, document);
        }
        catch
        {
            file.Dispose();
            throw;
        }
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

    private static void RevalidatePinnedProbe<TPayload>(
        FileProbe<TPayload> probe,
        string message)
    {
        if (probe.Handle is not null && probe.Identity is { } identity)
        {
            RevalidatePinnedHandle(probe.Handle, identity, message);
        }
    }

    private static void RevalidatePinnedHandle(
        ValidatedFileHandle handle,
        ValidatedFileIdentity expectedIdentity,
        string message)
    {
        try
        {
            handle.RevalidateIdentity();
        }
        catch (IOException exception)
        {
            throw new InvalidDataException(message, exception);
        }

        if (handle.Identity != expectedIdentity)
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

    private sealed class FileProbe<TPayload> : IDisposable
    {
        internal FileProbe(
            ValidatedFileHandle? handle,
            JsonDocumentEnvelope<TPayload>? document)
        {
            Handle = handle;
            Document = document;
        }

        internal static FileProbe<TPayload> Missing => new(null, null);

        internal ValidatedFileHandle? Handle { get; }

        internal ValidatedFileIdentity? Identity => Handle?.Identity;

        internal JsonDocumentEnvelope<TPayload>? Document { get; }

        internal bool Exists => Identity.HasValue;

        public void Dispose() => Handle?.Dispose();
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
    private bool _publicationStarted;

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

    internal ValidatedFileHandle BeginPublication()
    {
        if (_publicationStarted)
        {
            throw new InvalidOperationException("The prepared JSON write was already published.");
        }

        var stagingHandle = _stagingHandle ??
            throw new InvalidOperationException("The validated staging handle was already released.");
        _publicationStarted = true;
        return stagingHandle;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_stagingHandle is { HasBeenRenamed: false } stagingHandle)
            {
                _cleanup.Delete(stagingHandle);
            }
        }
        finally
        {
            try
            {
                if (_publicationStarted && _stagingHandle is not null)
                {
                    Owner.BeforePreparedHandleRelease();
                }
            }
            finally
            {
                _stagingHandle?.Dispose();
                _stagingHandle = null;
                _pathLease.Dispose();
                _disposed = true;
            }
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
        PublishOwned(prepared, expectedProjection: false);

    internal JsonDocumentEnvelope<TPayload> ReplaceProjection<TPayload>(
        PreparedJsonWrite<TPayload> prepared) =>
        PublishOwned(prepared, expectedProjection: true);

    private JsonDocumentEnvelope<TPayload> PublishOwned<TPayload>(
        PreparedJsonWrite<TPayload> prepared,
        bool expectedProjection)
    {
        ValidateOwnership(prepared);
        Exception? primaryFailure = null;
        JsonDocumentEnvelope<TPayload>? result = null;
        try
        {
            result = _owner.PublishPrepared(
                prepared,
                expectedProjection,
                _cancellationToken);
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
                "Atomic JSON transaction publication and prepared-handle cleanup both failed.",
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

    internal JsonDocumentEnvelope<TPayload> ReadAuthoritative<TPayload>(
        AuthoritativeJsonDestination destination) =>
        _owner.ReadAuthoritativeCore<TPayload>(destination, _cancellationToken);

    internal ProjectionJsonReadResult<TPayload> ReadProjection<TPayload>(
        ProjectionJsonDestination destination) =>
        _owner.ReadProjectionCore<TPayload>(destination, _cancellationToken);

    private void ValidateOwnership<TPayload>(PreparedJsonWrite<TPayload> prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(prepared.Owner, _owner))
        {
            throw new InvalidOperationException(
                "A transaction cannot take ownership of another atomic JSON authority's prepared write.");
        }
    }
}
