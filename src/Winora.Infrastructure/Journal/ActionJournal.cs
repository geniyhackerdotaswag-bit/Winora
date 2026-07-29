using Winora.Core.Contracts;
using Winora.Core.Journal;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;

namespace Winora.Infrastructure.Journal;

public sealed class ActionJournal : IActionJournal
{
    private readonly WinoraDataPaths _paths;
    private readonly IActionJournalOperationCatalog _operationCatalog;
    private readonly AtomicJsonFile _documents;
    private readonly JsonDocumentSerializer _serializer;
    private readonly IValidatedFileAccess _validatedFileAccess;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _eventIdProvider;

    public ActionJournal(
        WinoraDataPaths paths,
        IActionJournalOperationCatalog operationCatalog,
        TimeProvider? timeProvider = null)
        : this(
            paths,
            operationCatalog,
            new AtomicJsonFile(
                paths ?? throw new ArgumentNullException(nameof(paths)),
                (JsonDocumentSerializer?)null,
                timeProvider),
            timeProvider ?? TimeProvider.System,
            eventIdProvider: null)
    {
    }

    internal ActionJournal(
        WinoraDataPaths paths,
        IActionJournalOperationCatalog operationCatalog,
        AtomicJsonFile documents,
        TimeProvider timeProvider,
        Func<string>? eventIdProvider = null,
        JsonDocumentSerializer? serializer = null,
        IValidatedFileAccess? validatedFileAccess = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _operationCatalog = operationCatalog ??
            throw new ArgumentNullException(nameof(operationCatalog));
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _serializer = serializer ?? new JsonDocumentSerializer();
        _validatedFileAccess = validatedFileAccess ?? new WindowsValidatedFileAccess();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _eventIdProvider = eventIdProvider ?? (() => Guid.NewGuid().ToString("N"));
    }

    public async ValueTask<ActionJournalEntry> AppendAsync(
        ActionJournalEntryDraft draft,
        CancellationToken cancellationToken)
    {
        ActionJournalSchema.ValidateDraft(draft, _operationCatalog);
        cancellationToken.ThrowIfCancellationRequested();
        var eventId = _eventIdProvider();
        var document = ActionJournalDocument.Create(
            eventId,
            draft,
            _timeProvider.GetUtcNow(),
            _operationCatalog);
        await _documents.CreateNewAsync(
            _paths.GetJournalEventDocument(eventId),
            document,
            cancellationToken).ConfigureAwait(false);

        var entry = document.Rehydrate(_operationCatalog);
        await RefreshIndexBestEffortAsync().ConfigureAwait(false);
        return entry;
    }

    public async ValueTask<ActionJournalIndex> RebuildIndexAsync(
        CancellationToken cancellationToken)
    {
        var events = await ReadVerifiedEventsAsync(cancellationToken).ConfigureAwait(false);
        return await WriteIndexAsync(events, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ActionJournalIndex> WriteIndexAsync(
        IReadOnlyList<ActionJournalEntry> events,
        CancellationToken cancellationToken)
    {
        var rebuiltAtUtc = _timeProvider.GetUtcNow();
        ActionJournalSchema.ValidateTimestamp(rebuiltAtUtc, nameof(rebuiltAtUtc));
        var indexDocument = ActionJournalIndexDocument.Create(rebuiltAtUtc, events);
        await _documents.WriteProjectionAsync(
            _paths.JournalIndexDocument,
            indexDocument,
            cancellationToken).ConfigureAwait(false);
        return indexDocument.Rehydrate(_operationCatalog);
    }

    public async ValueTask<IReadOnlyList<ActionJournalEntry>> ReadAllAsync(
        CancellationToken cancellationToken)
    {
        var events = await ReadVerifiedEventsAsync(cancellationToken).ConfigureAwait(false);
        await RefreshIndexBestEffortAsync(events).ConfigureAwait(false);
        return events;
    }

    private async ValueTask<IReadOnlyList<ActionJournalEntry>> ReadVerifiedEventsAsync(
        CancellationToken cancellationToken)
    {
        var snapshots = await ReadVerifiedEventSnapshotsAsync(cancellationToken)
            .ConfigureAwait(false);
        return Array.AsReadOnly(snapshots.Select(item => item.Entry).ToArray());
    }

    internal async ValueTask<IReadOnlyList<VerifiedActionJournalEvent>> ReadVerifiedEventSnapshotsAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.JournalEventsDirectory))
        {
            return [];
        }

        using var directoryLease = SecureOwnedPathLease.AcquireExistingDirectory(
            _paths,
            _paths.JournalEventsDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.EnumerateDirectories(
                _paths.JournalEventsDirectory,
                "*",
                SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidDataException(
                "The action-journal event store contains an unexpected directory.");
        }

        var events = new List<VerifiedActionJournalEvent>();
        foreach (var path in Directory.EnumerateFiles(
                     _paths.JournalEventsDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDocumentedStagingArtifact(Path.GetFileName(path)))
            {
                continue;
            }

            if (!path.EndsWith(".json", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The action-journal event store contains an unexpected file.");
            }

            var eventId = ParseEventId(path);
            var read = await _documents.ReadAuthoritativeWithIdentityAsync<ActionJournalDocument>(
                _paths.GetJournalEventDocument(eventId),
                cancellationToken).ConfigureAwait(false);
            var entry = read.Document.Payload.Rehydrate(_operationCatalog);
            if (!StringComparer.Ordinal.Equals(entry.EventId, eventId))
            {
                throw new InvalidDataException(
                    "The action-journal filename, envelope identity, and payload identity must agree.");
            }

            events.Add(new VerifiedActionJournalEvent(
                entry,
                read.Document.PayloadSha256,
                read.Identity));
        }

        if (events.Select(item => item.Entry.EventId).Distinct(StringComparer.Ordinal).Count() != events.Count)
        {
            throw new InvalidDataException("The action journal contains duplicate event identifiers.");
        }

        return Array.AsReadOnly(events
            .OrderByDescending(item => item.Entry.TimestampUtc)
            .ThenByDescending(item => item.Entry.EventId, StringComparer.Ordinal)
            .ToArray());
    }

    internal async ValueTask<int> DeleteFromVerifiedRetentionIntentAsync(
        RetentionTransactionBoundary boundary,
        IMutationLeaseHandle lease,
        Func<ActionJournalEntry, CancellationToken, ValueTask> validateLinkedStateAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(validateLinkedStateAsync);
        RetentionMaintenanceSchema.Validate(boundary.Intent);
        if (boundary.State != RetentionLifecycleState.DeletingActionEvents)
        {
            throw new InvalidOperationException(
                "Action events may be pruned only from a verified durable deleting intent.");
        }

        if (lease.OperationId != boundary.Intent.TransactionId ||
            lease.LeaseId != boundary.LeaseId ||
            lease.Epoch != boundary.LeaseEpoch)
        {
            throw new InvalidOperationException(
                "Action-event deletion requires the exact lease bound to the durable deleting state.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_paths.JournalEventsDirectory))
        {
            return 0;
        }

        using var directoryLease = SecureOwnedPathLease.AcquireExistingDirectory(
            _paths,
            _paths.JournalEventsDirectory);
        var deleted = 0;
        foreach (var expected in boundary.Intent.ActionEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = _paths.GetJournalEventFile(expected.EventId);
            using var file = _validatedFileAccess.TryOpenForMutation(
                path,
                ValidatedFileUse.PublicRead);
            if (file is null)
            {
                // Absence is idempotent only because the caller supplied a verified
                // durable intent in the persisted DeletingActionEvents state.
                continue;
            }

            var expectedIdentity = new ValidatedFileIdentity(
                expected.VolumeSerialNumber,
                expected.FileIndex);
            if (file.Identity != expectedIdentity)
            {
                throw new InvalidDataException(
                    "An action-journal event identity changed after retention approval.");
            }

            var envelope = _serializer.DeserializeAndValidate<ActionJournalDocument>(
                file.ReadAllBytes(flushToDisk: false));
            if (!StringComparer.Ordinal.Equals(envelope.DocumentId, expected.EventId) ||
                !StringComparer.Ordinal.Equals(envelope.PayloadSha256, expected.PayloadSha256))
            {
                throw new InvalidDataException(
                    "An action-journal event changed after retention approval.");
            }

            var entry = envelope.Payload.Rehydrate(_operationCatalog);
            if (!StringComparer.Ordinal.Equals(entry.EventId, expected.EventId))
            {
                throw new InvalidDataException(
                    "The retained action-journal event identities do not agree.");
            }

            await validateLinkedStateAsync(entry, CancellationToken.None).ConfigureAwait(false);

            if (!await lease.RevalidateAsync(CancellationToken.None).ConfigureAwait(false))
            {
                throw new RetentionLeaseLostException();
            }

            file.MarkDelete();
            deleted++;
        }

        return deleted;
    }

    private async ValueTask RefreshIndexBestEffortAsync()
    {
        try
        {
            await RebuildIndexAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsProjectionCacheFailure(exception))
        {
            // The immutable event is already durable. The index is a cache and is rebuilt
            // from verified events whenever it is read.
        }
    }

    private async ValueTask RefreshIndexBestEffortAsync(
        IReadOnlyList<ActionJournalEntry> events)
    {
        try
        {
            await WriteIndexAsync(events, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsProjectionCacheFailure(exception))
        {
            // Reads return the verified immutable history even when the rebuildable cache
            // cannot be refreshed.
        }
    }

    private static bool IsProjectionCacheFailure(Exception exception) =>
        exception is IOException or InvalidDataException or UnauthorizedAccessException ||
        exception is AggregateException aggregate &&
        aggregate.InnerExceptions.Count > 0 &&
        aggregate.InnerExceptions.All(IsProjectionCacheFailure);

    private static string ParseEventId(string path)
    {
        var fileName = Path.GetFileName(path);
        if (!fileName.EndsWith(".json", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The action-journal event has an unsupported filename.");
        }

        var eventId = fileName[..^".json".Length];
        try
        {
            ActionJournalSchema.ValidateEventId(eventId, nameof(path));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "The action-journal event filename is invalid.",
                exception);
        }

        return eventId;
    }

    private static bool IsDocumentedStagingArtifact(string fileName)
    {
        const string jsonMarker = ".json.";
        const string temporarySuffix = ".tmp";
        if (!fileName.EndsWith(temporarySuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var markerIndex = fileName.IndexOf(jsonMarker, StringComparison.Ordinal);
        if (markerIndex <= 0 ||
            markerIndex != fileName.LastIndexOf(jsonMarker, StringComparison.Ordinal))
        {
            return false;
        }

        var eventId = fileName[..markerIndex];
        var stagingId = fileName[
            (markerIndex + jsonMarker.Length)..^temporarySuffix.Length];
        return IsCanonicalGuid(eventId) && IsCanonicalGuid(stagingId);
    }

    private static bool IsCanonicalGuid(string value) =>
        Guid.TryParseExact(value, "N", out var parsed) &&
        StringComparer.Ordinal.Equals(parsed.ToString("N"), value);
}

internal sealed record VerifiedActionJournalEvent(
    ActionJournalEntry Entry,
    string PayloadSha256,
    ValidatedFileIdentity Identity);
