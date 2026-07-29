using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;
using Winora.Core.Contracts;

namespace Winora.Infrastructure.Journal;

internal sealed class DurableRetentionJournal
{
    private readonly WinoraDataPaths _paths;
    private readonly AtomicJsonFile _documents;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal DurableRetentionJournal(
        WinoraDataPaths paths,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _documents = new AtomicJsonFile(_paths, (JsonDocumentSerializer?)null, _timeProvider);
    }

    internal DurableRetentionJournal(
        WinoraDataPaths paths,
        AtomicJsonFile documents,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async ValueTask<RetentionTransactionBoundary> CreateApprovedAsync(
        Guid transactionId,
        IMutationLeaseHandle lease,
        ActionJournalRetentionRequest request,
        RetentionArtifactSelection selection,
        CancellationToken cancellationToken)
    {
        ValidateTransactionAndLease(transactionId, lease);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selection);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = GetUtcNow();
            var intent = RetentionIntentDocument.Create(
                transactionId,
                now,
                lease,
                request,
                selection);
            var key = transactionId.ToString("N");
            await _documents.CreateNewAsync(
                _paths.GetRetentionIntentDocument(key),
                intent,
                cancellationToken).ConfigureAwait(false);
            var state = new RetentionStateDocument(
                RetentionStateDocument.CurrentSchemaVersion,
                transactionId,
                RetentionLifecycleState.Approved,
                Revision: 1,
                lease.LeaseId,
                lease.Epoch,
                now);
            RetentionMaintenanceSchema.Validate(state, intent);
            await _documents.WriteProjectionAsync(
                _paths.GetRetentionStateDocument(key),
                state,
                cancellationToken).ConfigureAwait(false);
            return ToBoundary(intent, state);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<RetentionTransactionBoundary> ReadAsync(
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        ValidateTransactionId(transactionId);
        var key = transactionId.ToString("N");
        var envelope = await _documents.ReadAuthoritativeAsync<RetentionIntentDocument>(
            _paths.GetRetentionIntentDocument(key),
            cancellationToken).ConfigureAwait(false);
        var intent = envelope.Payload;
        RetentionMaintenanceSchema.Validate(intent);
        if (intent.TransactionId != transactionId)
        {
            throw new InvalidDataException(
                "The retention directory and immutable transaction identity do not agree.");
        }

        try
        {
            var stateRead = await _documents.ReadProjectionAsync<RetentionStateDocument>(
                _paths.GetRetentionStateDocument(key),
                cancellationToken).ConfigureAwait(false);
            RetentionMaintenanceSchema.Validate(stateRead.Document.Payload, intent);
            return ToBoundary(intent, stateRead.Document.Payload);
        }
        catch (FileNotFoundException)
        {
            // The immutable approved intent is authoritative. A crash between its
            // publication and the initial projection cannot lose recovery authority.
            return new RetentionTransactionBoundary(
                intent,
                RetentionLifecycleState.Approved,
                Revision: 0,
                intent.ApprovedLeaseId,
                intent.ApprovedLeaseEpoch,
                intent.ApprovedUtc);
        }
    }

    internal async ValueTask<IReadOnlyList<RetentionTransactionBoundary>> ScanIncompleteAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_paths.JournalRetentionDirectory))
        {
            return [];
        }

        using var rootLease = SecureOwnedPathLease.AcquireExistingDirectory(
            _paths,
            _paths.JournalRetentionDirectory);
        if (Directory.EnumerateFiles(
                _paths.JournalRetentionDirectory,
                "*",
                SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidDataException(
                "The retention lifecycle store contains an unexpected root file.");
        }

        var incomplete = new List<RetentionTransactionBoundary>();
        foreach (var directory in Directory.EnumerateDirectories(
                     _paths.JournalRetentionDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Path.GetFileName(directory);
            if (!IsCanonicalGuid(key))
            {
                throw new InvalidDataException(
                    "The retention lifecycle store contains an invalid transaction directory.");
            }

            ValidateTransactionDirectory(directory, key);
            var intentPath = _paths.GetRetentionIntentFile(key);
            if (!File.Exists(intentPath))
            {
                // A documented staging-only directory predates durable approval and
                // therefore cannot authorize mutation or recovery.
                continue;
            }

            var boundary = await ReadAsync(Guid.ParseExact(key, "N"), cancellationToken)
                .ConfigureAwait(false);
            if (boundary.State != RetentionLifecycleState.Completed)
            {
                incomplete.Add(boundary);
            }
        }

        return Array.AsReadOnly(incomplete
            .OrderBy(item => item.Intent.ApprovedUtc)
            .ThenBy(item => item.Intent.TransactionId)
            .ToArray());
    }

    internal async ValueTask<RetentionTransactionBoundary> ClaimAsync(
        RetentionTransactionBoundary expected,
        IMutationLeaseHandle lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ValidateTransactionAndLease(expected.Intent.TransactionId, lease);
        if (expected.LeaseId == lease.LeaseId && expected.LeaseEpoch == lease.Epoch)
        {
            return expected;
        }

        if (!lease.IsRecoveryTakeover || lease.Epoch <= expected.LeaseEpoch)
        {
            throw new InvalidOperationException(
                "A different lease may resume retention only as a higher-epoch recovery takeover.");
        }

        return await WriteStateAsync(
            expected,
            expected.State,
            lease,
            cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<RetentionTransactionBoundary> AdvanceAsync(
        RetentionTransactionBoundary expected,
        RetentionLifecycleState next,
        IMutationLeaseHandle lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ValidateTransactionAndLease(expected.Intent.TransactionId, lease);
        if (expected.LeaseId != lease.LeaseId || expected.LeaseEpoch != lease.Epoch)
        {
            throw new InvalidOperationException(
                "Retention state can advance only under its currently persisted lease epoch.");
        }

        if (!IsAllowedTransition(expected.State, next))
        {
            throw new InvalidOperationException(
                $"Retention cannot advance from {expected.State} to {next}.");
        }

        return WriteStateAsync(expected, next, lease, cancellationToken);
    }

    private async ValueTask<RetentionTransactionBoundary> WriteStateAsync(
        RetentionTransactionBoundary expected,
        RetentionLifecycleState next,
        IMutationLeaseHandle lease,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(
                expected.Intent.TransactionId,
                cancellationToken).ConfigureAwait(false);
            if (current.State != expected.State ||
                current.Revision != expected.Revision ||
                current.LeaseId != expected.LeaseId ||
                current.LeaseEpoch != expected.LeaseEpoch)
            {
                throw new InvalidOperationException(
                    "The durable retention state changed before the requested transition.");
            }

            var state = new RetentionStateDocument(
                RetentionStateDocument.CurrentSchemaVersion,
                expected.Intent.TransactionId,
                next,
                checked(expected.Revision + 1),
                lease.LeaseId,
                lease.Epoch,
                GetUtcNow());
            RetentionMaintenanceSchema.Validate(state, expected.Intent);
            var key = expected.Intent.TransactionId.ToString("N");
            await _documents.WriteProjectionAsync(
                _paths.GetRetentionStateDocument(key),
                state,
                cancellationToken).ConfigureAwait(false);
            return ToBoundary(expected.Intent, state);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsAllowedTransition(
        RetentionLifecycleState current,
        RetentionLifecycleState next) =>
        (current, next) is
            (RetentionLifecycleState.Approved, RetentionLifecycleState.DeletingOperation) or
            (RetentionLifecycleState.Approved, RetentionLifecycleState.OperationDeleted) or
            (RetentionLifecycleState.DeletingOperation, RetentionLifecycleState.OperationDeleted) or
            (RetentionLifecycleState.OperationDeleted, RetentionLifecycleState.DeletingBackup) or
            (RetentionLifecycleState.OperationDeleted, RetentionLifecycleState.BackupDeleted) or
            (RetentionLifecycleState.DeletingBackup, RetentionLifecycleState.BackupDeleted) or
            (RetentionLifecycleState.BackupDeleted, RetentionLifecycleState.DeletingActionEvents) or
            (RetentionLifecycleState.BackupDeleted, RetentionLifecycleState.ActionEventsDeleted) or
            (RetentionLifecycleState.DeletingActionEvents, RetentionLifecycleState.ActionEventsDeleted) or
            (RetentionLifecycleState.ActionEventsDeleted, RetentionLifecycleState.Completed);

    private static RetentionTransactionBoundary ToBoundary(
        RetentionIntentDocument intent,
        RetentionStateDocument state) =>
        new(
            intent,
            state.State,
            state.Revision,
            state.LeaseId,
            state.LeaseEpoch,
            state.UpdatedUtc);

    private static void ValidateTransactionAndLease(
        Guid transactionId,
        IMutationLeaseHandle lease)
    {
        ValidateTransactionId(transactionId);
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.OperationId != transactionId ||
            lease.LeaseId == Guid.Empty ||
            lease.Epoch <= 0)
        {
            throw new InvalidOperationException(
                "The held global mutation lease must be bound to the exact retention transaction.");
        }
    }

    private static void ValidateTransactionId(Guid transactionId)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A retention transaction identifier is required.",
                nameof(transactionId));
        }
    }

    private DateTimeOffset GetUtcNow()
    {
        var now = _timeProvider.GetUtcNow();
        if (now == default || now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "The retention lifecycle clock must return a non-default UTC timestamp.");
        }

        return now;
    }

    private static void ValidateTransactionDirectory(string directory, string key)
    {
        if (Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidDataException(
                "A retention transaction contains an unexpected directory.");
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (name is "intent.json" or "state.json" or "state.json.last-known-good" ||
                IsDocumentedStagingArtifact(name, "intent.json") ||
                IsDocumentedStagingArtifact(name, "state.json"))
            {
                continue;
            }

            throw new InvalidDataException(
                $"Retention transaction {key} contains an unexpected file.");
        }
    }

    private static bool IsDocumentedStagingArtifact(string fileName, string documentName)
    {
        var prefix = $"{documentName}.";
        const string suffix = ".tmp";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var stagingId = fileName[prefix.Length..^suffix.Length];
        return IsCanonicalGuid(stagingId);
    }

    private static bool IsCanonicalGuid(string value) =>
        Guid.TryParseExact(value, "N", out var parsed) &&
        StringComparer.Ordinal.Equals(value, parsed.ToString("N"));
}
