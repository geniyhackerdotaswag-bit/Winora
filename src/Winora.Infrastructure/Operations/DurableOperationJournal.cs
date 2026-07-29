using System.ComponentModel;
using System.Globalization;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Infrastructure.Backups;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;

namespace Winora.Infrastructure.Operations;

public sealed class DurableOperationJournal : IDurableOperationJournal
{
    private readonly WinoraDataPaths _paths;
    private readonly DurableJournalActor _actor;
    private readonly AtomicJsonFile _documents;
    private readonly Action<string>? _beforeDirectoryLease;

    public DurableOperationJournal(
        WinoraDataPaths paths,
        DurableJournalActor actor,
        TimeProvider? timeProvider = null)
        : this(
            paths,
            actor,
            new AtomicJsonFile(
                paths ?? throw new ArgumentNullException(nameof(paths)),
                serializer: null,
                timeProvider: timeProvider))
    {
    }

    internal DurableOperationJournal(
        WinoraDataPaths paths,
        DurableJournalActor actor,
        Action<string> beforeDirectoryLease)
        : this(
            paths,
            actor,
            new AtomicJsonFile(
                paths ?? throw new ArgumentNullException(nameof(paths)),
                (JsonDocumentSerializer?)null,
                null),
            beforeDirectoryLease)
    {
    }

    internal DurableOperationJournal(
        WinoraDataPaths paths,
        DurableJournalActor actor,
        AtomicJsonFile documents,
        Action<string>? beforeDirectoryLease = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        if (!Enum.IsDefined(actor))
        {
            throw new ArgumentOutOfRangeException(nameof(actor));
        }

        _actor = actor;
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _beforeDirectoryLease = beforeDirectoryLease;
    }

    public async ValueTask<DurableOperationBoundary?> ReadVerifiedBoundaryAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ValidateOperationId(operationId);
        var operationKey = operationId.ToString("N");
        var snapshot = await ReadVerifiedChainAsync(
            operationId,
            operationKey,
            cancellationToken).ConfigureAwait(false);
        await RefreshProjectionBestEffortAsync(
            operationKey,
            snapshot.Chain).ConfigureAwait(false);
        return snapshot.Chain.Boundary;
    }

    public async ValueTask<IReadOnlyList<DurableOperationBoundary>> ScanIncompleteAsync(
        CancellationToken cancellationToken)
    {
        var chains = await ScanVerifiedChainsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in chains)
        {
            await RefreshProjectionBestEffortAsync(
                item.OperationKey,
                item.Snapshot.Chain).ConfigureAwait(false);
        }

        return Array.AsReadOnly(chains
            .Select(item => item.Snapshot.Chain.Boundary)
            .Where(boundary =>
                boundary is not null &&
                !OperationStatePolicy.IsTerminal(boundary.State))
            .Cast<DurableOperationBoundary>()
            .ToArray());
    }

    internal async ValueTask<IReadOnlyList<OperationStorageCatalogEntry>> ScanStorageCatalogAsync(
        CancellationToken cancellationToken)
    {
        var chains = await ScanVerifiedChainsAsync(cancellationToken).ConfigureAwait(false);
        return Array.AsReadOnly(chains
            .Where(item =>
                item.Snapshot.Chain.Boundary is not null &&
                item.Snapshot.Chain.LastEvent is not null)
            .Select(item => OperationStorageCatalogEntry.Create(
                item.Snapshot.Chain.Boundary!,
                item.Snapshot.Chain.LastEvent!,
                item.RootIdentity))
            .ToArray());
    }

    internal async ValueTask<bool> DeleteVerifiedTerminalAsync(
        OperationStorageCatalogEntry expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!expected.IsTerminal || expected.IsRecoveryProtected)
        {
            throw new InvalidOperationException(
                "Incomplete or recovery-required operation history cannot be deleted.");
        }

        var operationKey = expected.OperationId.ToString("N");
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperationDirectoryExists(_paths.GetOperationDirectory(operationKey)))
        {
            // The exact immutable retention intent remains the authority after a crash
            // between the handle-bound tree deletion and its lifecycle state advance.
            return false;
        }

        using (var rootIdentity = SecureBackupDirectoryLayout.AcquirePinnedDirectory(
                   _paths.GetOperationDirectory(operationKey),
                   allowRename: false))
        {
            EnsureRootIdentity(expected, rootIdentity.Identity);
        }

        var snapshot = await ReadVerifiedChainAsync(
            expected.OperationId,
            operationKey,
            cancellationToken).ConfigureAwait(false);
        EnsureRetentionSnapshot(expected, snapshot.Chain);
        return await _documents.ExecuteTransactionAsync(
            transaction => DeleteVerifiedTerminal(
                expected,
                snapshot,
                transaction),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DurableTransitionResult> CompareAndAppendAsync(
        OperationTransition transition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ValidateOperationId(transition.OperationId);
        cancellationToken.ThrowIfCancellationRequested();

        var operationKey = transition.OperationId.ToString("N");
        var snapshot = await ReadVerifiedChainAsync(
            transition.OperationId,
            operationKey,
            cancellationToken).ConfigureAwait(false);
        var currentRevision = snapshot.Chain.Boundary?.Revision ?? 0;
        var currentState = snapshot.Chain.Boundary?.State;
        if (currentRevision != transition.ExpectedRevision ||
            currentState != transition.ExpectedState)
        {
            return DurableTransitionResult.Rejected(currentRevision, currentState);
        }

        var document = OperationTransitionDocument.Create(
            transition,
            _actor,
            snapshot.Chain.LastEvent?.EventHash);
        var destination = _paths.GetOperationTransitionDocument(
            operationKey,
            document.Revision,
            document.TransitionId);
        var prepared = _documents.PrepareAuthoritative(
            destination,
            document,
            cancellationToken);

        var append = await _documents.ExecutePreparedAsync(
            prepared,
            transaction => CompareAndPublish(
                transition,
                operationKey,
                document,
                snapshot,
                prepared,
                transaction),
            cancellationToken).ConfigureAwait(false);
        if (append.CatalogChanged)
        {
            var latest = await ReadVerifiedChainAsync(
                transition.OperationId,
                operationKey,
                cancellationToken).ConfigureAwait(false);
            return DurableTransitionResult.Rejected(
                latest.Chain.Boundary?.Revision ?? 0,
                latest.Chain.Boundary?.State);
        }

        if (!append.Result.IsDurable)
        {
            return append.Result;
        }

        // The immutable event is already durable. Projection failures must never make a
        // caller retry and accidentally report an already-committed mutation as failed.
        await RefreshProjectionBestEffortAsync(operationKey, append.Chain).ConfigureAwait(false);
        return append.Result;
    }

    private AppendOutcome CompareAndPublish(
        OperationTransition transition,
        string operationKey,
        OperationTransitionDocument document,
        VerifiedOperationSnapshot snapshot,
        PreparedJsonWrite<OperationTransitionDocument> prepared,
        AtomicJsonTransaction transaction)
    {
        if (!TailMatchesSnapshot(operationKey, snapshot, transaction))
        {
            return AppendOutcome.Changed(snapshot.Chain);
        }

        var candidate = VerifiedOperationChain.Rebuild(
            transition.OperationId,
            snapshot.Chain.Events.Append(document));
        transaction.PublishNew(prepared);
        return new AppendOutcome(
            DurableTransitionResult.Acknowledged(transition, document.Revision),
            candidate,
            CatalogChanged: false);
    }

    private async ValueTask<VerifiedOperationSnapshot> ReadVerifiedChainAsync(
        Guid operationId,
        string operationKey,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ReadVerifiedChainOnceAsync(
                    operationId,
                    operationKey,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception) when (
                attempt < maximumAttempts &&
                IsSharingViolation(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<VerifiedOperationSnapshot> ReadVerifiedChainOnceAsync(
        Guid operationId,
        string operationKey,
        CancellationToken cancellationToken)
    {
        var transitionsDirectory = Path.Combine(
            _paths.GetOperationDirectory(operationKey),
            "Transitions");
        if (!Directory.Exists(transitionsDirectory))
        {
            return new VerifiedOperationSnapshot(
                VerifiedOperationChain.Rebuild(operationId, []),
                LastEventIdentity: null);
        }

        _beforeDirectoryLease?.Invoke(transitionsDirectory);
        using var directoryLease = SecureOwnedPathLease.AcquireExistingDirectory(
            _paths,
            transitionsDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = new List<ParsedTransitionFile>();
        foreach (var filePath in Directory.EnumerateFiles(
                     transitionsDirectory,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            parsed.Add(ParseTransitionFile(filePath));
        }

        if (parsed.Select(item => item.Revision).Distinct().Count() != parsed.Count ||
            parsed.Select(item => item.TransitionId)
                .Distinct(StringComparer.Ordinal)
                .Count() != parsed.Count)
        {
            throw new InvalidDataException(
                "The authoritative operation log contains a duplicate revision or transition identifier.");
        }

        var documents = new List<OperationTransitionDocument>(parsed.Count);
        ValidatedFileIdentity? lastEventIdentity = null;
        foreach (var item in parsed.OrderBy(item => item.Revision))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = _paths.GetOperationTransitionDocument(
                operationKey,
                item.Revision,
                item.TransitionId);
            var read = await _documents.ReadAuthoritativeWithIdentityAsync<OperationTransitionDocument>(
                destination,
                cancellationToken).ConfigureAwait(false);
            var document = read.Document.Payload;
            if (document.Revision != item.Revision ||
                !StringComparer.Ordinal.Equals(document.TransitionId, item.TransitionId))
            {
                throw new InvalidDataException(
                    "A transition filename, envelope identity, and payload identity must agree.");
            }

            documents.Add(document);
            lastEventIdentity = read.Identity;
        }

        return new VerifiedOperationSnapshot(
            VerifiedOperationChain.Rebuild(operationId, documents),
            lastEventIdentity);
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) == 32 ||
        exception.InnerException is Win32Exception { NativeErrorCode: 32 };

    private static bool OperationDirectoryExists(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "The retained operation root is not an ordinary directory.");
            }

            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private async ValueTask<IReadOnlyList<OperationCatalogItem>> ScanVerifiedChainsAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.OperationsDirectory))
        {
            return [];
        }

        using var catalogLease = SecureOwnedPathLease.AcquireExistingDirectory(
            _paths,
            _paths.OperationsDirectory);
        if (Directory.EnumerateFiles(
                _paths.OperationsDirectory,
                "*",
                SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidDataException(
                "The fixed operation store contains an unexpected root file.");
        }

        var items = new List<OperationCatalogItem>();
        foreach (var directory in Directory.EnumerateDirectories(
                     _paths.OperationsDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operationKey = Path.GetFileName(directory);
            if (!Guid.TryParseExact(operationKey, "N", out var operationId) ||
                !StringComparer.Ordinal.Equals(operationId.ToString("N"), operationKey))
            {
                throw new InvalidDataException(
                    "The fixed operation store contains an invalid operation identifier.");
            }

            using var operationLease = SecureOwnedPathLease.AcquireExistingDirectory(
                _paths,
                directory);
            using var rootIdentity = SecureBackupDirectoryLayout.AcquirePinnedDirectory(
                directory,
                allowRename: false);
            items.Add(new OperationCatalogItem(
                operationKey,
                await ReadVerifiedChainAsync(
                    operationId,
                    operationKey,
                    cancellationToken).ConfigureAwait(false),
                rootIdentity.Identity));
        }

        return Array.AsReadOnly(items
            .OrderBy(item => item.OperationKey, StringComparer.Ordinal)
            .ToArray());
    }

    private bool TailMatchesSnapshot(
        string operationKey,
        VerifiedOperationSnapshot snapshot,
        AtomicJsonTransaction transaction)
    {
        var lastEvent = snapshot.Chain.LastEvent;
        if (lastEvent is not null)
        {
            ValidatedJsonRead<OperationTransitionDocument> currentTail;
            try
            {
                currentTail = transaction.ReadAuthoritativeWithIdentity<OperationTransitionDocument>(
                    _paths.GetOperationTransitionDocument(
                        operationKey,
                        lastEvent.Revision,
                        lastEvent.TransitionId));
            }
            catch (FileNotFoundException)
            {
                return false;
            }

            if (currentTail.Identity != snapshot.LastEventIdentity ||
                !StringComparer.Ordinal.Equals(
                    currentTail.Document.Payload.EventHash,
                    lastEvent.EventHash) ||
                !StringComparer.Ordinal.Equals(
                    currentTail.Document.Payload.TransitionId,
                    lastEvent.TransitionId) ||
                currentTail.Document.Payload.Revision != lastEvent.Revision)
            {
                return false;
            }
        }

        var transitionsDirectory = Path.Combine(
            _paths.GetOperationDirectory(operationKey),
            "Transitions");
        var nextRevision = (snapshot.Chain.Boundary?.Revision ?? 0) + 1;
        return !Directory.EnumerateFiles(
                transitionsDirectory,
                $"{nextRevision.ToString(CultureInfo.InvariantCulture)}-*.json",
                SearchOption.TopDirectoryOnly)
            .Any();
    }

    private bool DeleteVerifiedTerminal(
        OperationStorageCatalogEntry expected,
        VerifiedOperationSnapshot snapshot,
        AtomicJsonTransaction transaction)
    {
        var operationKey = expected.OperationId.ToString("N");
        var operationDirectory = _paths.GetOperationDirectory(operationKey);
        if (!Directory.Exists(operationDirectory))
        {
            return false;
        }

        if (!TailMatchesSnapshot(operationKey, snapshot, transaction))
        {
            throw new InvalidDataException(
                "The operation history changed after retention catalog verification.");
        }

        EnsureRetentionSnapshot(expected, snapshot.Chain);

        using var rootLease = SecureOwnedPathLease.AcquireExistingDirectory(
            _paths,
            _paths.OperationsDirectory);
        SecureBackupDirectoryLayout.DirectoryIdentity expectedIdentity;
        using (var identityLease = SecureBackupDirectoryLayout.AcquirePinnedDirectory(
                   operationDirectory,
                   allowRename: true))
        {
            if (!identityLease.MatchesPath(operationDirectory))
            {
                throw new IOException(
                    "The operation directory identity changed before retention deletion.");
            }

            EnsureRootIdentity(expected, identityLease.Identity);

            expectedIdentity = identityLease.Identity;
        }

        SecureBackupDirectoryLayout.DeleteTreeWithoutFollowingReparsePoints(
            operationDirectory,
            expectedIdentity);
        return true;
    }

    private static void EnsureRetentionSnapshot(
        OperationStorageCatalogEntry expected,
        VerifiedOperationChain chain)
    {
        if (chain.Boundary is null || chain.LastEvent is null)
        {
            throw new InvalidDataException(
                "The operation history no longer has a verified durable boundary.");
        }

        var current = OperationStorageCatalogEntry.Create(
            chain.Boundary,
            chain.LastEvent,
            new SecureBackupDirectoryLayout.DirectoryIdentity(
                expected.RootVolumeSerialNumber,
                expected.RootFileIndex));
        if (current != expected)
        {
            throw new InvalidDataException(
                "The operation history changed after retention catalog verification.");
        }

        if (!current.IsTerminal || current.IsRecoveryProtected)
        {
            throw new InvalidOperationException(
                "Only verified terminal operation history may be deleted.");
        }
    }

    private static void EnsureRootIdentity(
        OperationStorageCatalogEntry expected,
        SecureBackupDirectoryLayout.DirectoryIdentity actual)
    {
        if (actual.VolumeSerialNumber != expected.RootVolumeSerialNumber ||
            actual.FileIndex != expected.RootFileIndex)
        {
            throw new InvalidDataException(
                "The operation root identity changed after retention catalog verification.");
        }
    }

    private async ValueTask RefreshProjectionBestEffortAsync(
        string operationKey,
        VerifiedOperationChain chain)
    {
        if (chain.Boundary is null || chain.LastEvent is null)
        {
            return;
        }

        try
        {
            await _documents.WriteProjectionAsync(
                _paths.GetOperationManifestDocument(operationKey),
                OperationProjectionDocument.From(
                    chain.Boundary,
                    chain.LastEvent.EventHash),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsProjectionCacheFailure(exception))
        {
            // This cache is rebuilt from immutable events on every read. Its loss never
            // weakens the durable mutation boundary.
        }
    }

    private static bool IsProjectionCacheFailure(Exception exception) =>
        exception is IOException or InvalidDataException or UnauthorizedAccessException ||
        exception is AggregateException aggregate &&
        aggregate.InnerExceptions.Count > 0 &&
        aggregate.InnerExceptions.All(IsProjectionCacheFailure);

    private static ParsedTransitionFile ParseTransitionFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        const string extension = ".json";
        if (!fileName.EndsWith(extension, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A transition file has an unsupported extension.");
        }

        var stem = fileName[..^extension.Length];
        var separator = stem.IndexOf('-');
        if (separator <= 0 || separator == stem.Length - 1 ||
            !long.TryParse(
                stem.AsSpan(0, separator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var revision) ||
            revision <= 0 ||
            !StringComparer.Ordinal.Equals(
                revision.ToString(CultureInfo.InvariantCulture),
                stem[..separator]))
        {
            throw new InvalidDataException("A transition filename has an invalid revision.");
        }

        var transitionId = stem[(separator + 1)..];
        if (transitionId.Length != 32 ||
            transitionId.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException("A transition filename has an invalid transition identifier.");
        }

        return new ParsedTransitionFile(revision, transitionId);
    }

    private static void ValidateOperationId(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A durable operation identifier is required.",
                nameof(operationId));
        }
    }

    private sealed record ParsedTransitionFile(long Revision, string TransitionId);

    private sealed record AppendOutcome(
        DurableTransitionResult Result,
        VerifiedOperationChain Chain,
        bool CatalogChanged)
    {
        internal static AppendOutcome Changed(VerifiedOperationChain chain) =>
            new(
                DurableTransitionResult.Rejected(
                    chain.Boundary?.Revision ?? 0,
                    chain.Boundary?.State),
                chain,
                CatalogChanged: true);
    }

    private sealed record VerifiedOperationSnapshot(
        VerifiedOperationChain Chain,
        ValidatedFileIdentity? LastEventIdentity);

    private sealed record OperationCatalogItem(
        string OperationKey,
        VerifiedOperationSnapshot Snapshot,
        SecureBackupDirectoryLayout.DirectoryIdentity RootIdentity);
}

internal sealed record OperationStorageCatalogEntry(
    Guid OperationId,
    long Revision,
    OperationState State,
    string LastEventHash,
    DateTimeOffset TerminalOccurredAtUtc,
    string PlanDigest,
    string? BackupId,
    string? BackupDigest,
    uint RootVolumeSerialNumber,
    ulong RootFileIndex,
    bool IsTerminal,
    bool IsRecoveryProtected)
{
    internal static OperationStorageCatalogEntry Create(
        DurableOperationBoundary boundary,
        OperationTransitionDocument lastEvent,
        SecureBackupDirectoryLayout.DirectoryIdentity rootIdentity)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(lastEvent);
        if (lastEvent.OperationId != boundary.OperationId ||
            lastEvent.Revision != boundary.Revision ||
            lastEvent.OccurredAtUtc == default ||
            lastEvent.OccurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "The operation catalog terminal timestamp is not authoritative.");
        }

        var terminal = OperationStatePolicy.IsTerminal(boundary.State);
        return new OperationStorageCatalogEntry(
            boundary.OperationId,
            boundary.Revision,
            boundary.State,
            lastEvent.EventHash,
            lastEvent.OccurredAtUtc,
            boundary.Facts.PlanDigest,
            boundary.Facts.BackupId,
            boundary.Facts.BackupDigest,
            rootIdentity.VolumeSerialNumber,
            rootIdentity.FileIndex,
            terminal,
            !terminal);
    }
}
