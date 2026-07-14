using System.Diagnostics;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;
using Xunit;

namespace Winora.Infrastructure.Tests.Persistence;

public sealed class AtomicJsonHardeningTests
{
    [Fact]
    public void Public_atomic_api_accepts_only_fixed_layout_typed_destinations()
    {
        var publicMethods = typeof(AtomicJsonFile).GetMethods()
            .Where(method => method.DeclaringType == typeof(AtomicJsonFile) && method.IsPublic)
            .ToArray();

        Assert.DoesNotContain(
            publicMethods,
            method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string)));
        Assert.False(typeof(IWriteThroughPublisher).IsPublic);
        Assert.False(typeof(IAtomicFileOperations).IsPublic);
        Assert.False(typeof(IFileDurability).IsPublic);
        Assert.DoesNotContain(
            typeof(IAtomicFileCleanup).GetMethods(),
            method => method.GetParameters().Any(
                parameter => parameter.ParameterType == typeof(string)));
    }

    [Fact]
    public async Task Projection_recovery_is_explicit_and_bound_to_the_destination_identity()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var file = CreateFile(paths);
        var destination = paths.ChangeIndexDocument;
        await file.WriteProjectionAsync(destination, new ValuePayload(1), CancellationToken.None);
        await file.WriteProjectionAsync(destination, new ValuePayload(2), CancellationToken.None);
        await File.WriteAllTextAsync(destination.FilePath, "corrupt");

        var recovered = await file.ReadProjectionAsync<ValuePayload>(destination, CancellationToken.None);

        Assert.Equal(ProjectionReadSource.LastKnownGood, recovered.Source);
        Assert.Equal(1, recovered.Document.Payload.Value);
    }

    [Fact]
    public async Task Projection_recovery_rejects_a_self_consistent_last_known_good_with_the_wrong_id()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var file = CreateFile(paths);
        var destination = paths.ChangeIndexDocument;
        var serializer = new JsonDocumentSerializer();
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(destination.FilePath, "corrupt");
        await File.WriteAllBytesAsync(
            destination.LastKnownGoodFilePath,
            serializer.Serialize(serializer.CreateEnvelope(
                "recovery-index",
                new DateTimeOffset(2026, 7, 13, 8, 30, 0, TimeSpan.Zero),
                new ValuePayload(1))));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            file.ReadProjectionAsync<ValuePayload>(destination, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Authoritative_read_never_substitutes_a_last_known_good_sidecar()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var file = CreateFile(paths);
        var destination = paths.GetJournalEventDocument("event-id");
        var serializer = new JsonDocumentSerializer();
        Directory.CreateDirectory(paths.JournalEventsDirectory);
        await File.WriteAllBytesAsync(
            destination.FilePath + ".last-known-good",
            serializer.Serialize(serializer.CreateEnvelope(
                destination.DocumentId,
                new DateTimeOffset(2026, 7, 13, 8, 30, 0, TimeSpan.Zero),
                new ValuePayload(1))));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            file.ReadAuthoritativeAsync<ValuePayload>(destination, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Mutation_rejects_a_junction_root_without_writing_through_it()
    {
        using var container = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var linkedRoot = container.File("linked-root");
        CreateJunction(linkedRoot, outside.Path);
        var paths = new WinoraDataPaths(linkedRoot);
        var file = CreateFile(paths);

        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                file.WriteProjectionAsync(
                    paths.ChangeIndexDocument,
                    new ValuePayload(1),
                    CancellationToken.None).AsTask());
            Assert.False(File.Exists(Path.Combine(outside.Path, "Data", "change-index.json")));
        }
        finally
        {
            Directory.Delete(linkedRoot);
        }
    }

    [Fact]
    public async Task Mutation_rejects_a_junction_in_a_mutable_ancestor_without_writing_outside_root()
    {
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        CreateJunction(paths.DataDirectory, outside.Path);
        var file = CreateFile(paths);

        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                file.WriteProjectionAsync(
                    paths.ChangeIndexDocument,
                    new ValuePayload(1),
                    CancellationToken.None).AsTask());
            Assert.False(File.Exists(Path.Combine(outside.Path, "change-index.json")));
        }
        finally
        {
            Directory.Delete(paths.DataDirectory);
        }
    }

    [Fact]
    public async Task Slow_staging_does_not_hold_the_global_persistence_mutex()
    {
        using var firstRoot = new TemporaryDirectory();
        using var secondRoot = new TemporaryDirectory();
        using var stagingGate = new BlockingFlushDurability();
        var firstPaths = new WinoraDataPaths(firstRoot.Path);
        var secondPaths = new WinoraDataPaths(secondRoot.Path);
        var first = new AtomicJsonFile(
            firstPaths,
            fileDurability: stagingGate,
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 13, 8, 30, 0, TimeSpan.Zero)));
        var publishEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new AtomicJsonFile(
            secondPaths,
            publisher: new SignalingPublisher(publishEntered),
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 13, 8, 30, 0, TimeSpan.Zero)));

        var firstWrite = Task.Run(async () =>
            await first.CreateNewAsync(
                firstPaths.GetJournalEventDocument("first-event"),
                new ValuePayload(1),
                CancellationToken.None));
        Assert.True(stagingGate.WaitUntilBlocked(TimeSpan.FromSeconds(5)));

        Task? secondWrite = null;
        try
        {
            secondWrite = second.CreateNewAsync(
                secondPaths.GetJournalEventDocument("second-event"),
                new ValuePayload(2),
                CancellationToken.None).AsTask();
            await publishEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await secondWrite;
        }
        finally
        {
            stagingGate.Release();
            await firstWrite;
            if (secondWrite is not null)
            {
                await secondWrite;
            }
        }
    }

    [Fact]
    public async Task Mutable_ancestor_cannot_be_swapped_after_validation_while_staging_is_in_flight()
    {
        using var root = new TemporaryDirectory();
        using var stagingGate = new BlockingFlushDurability();
        var paths = new WinoraDataPaths(root.Path);
        var file = new AtomicJsonFile(
            paths,
            fileDurability: stagingGate,
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 13, 8, 30, 0, TimeSpan.Zero)));
        var write = Task.Run(async () =>
            await file.CreateNewAsync(
                paths.GetJournalEventDocument("event-id"),
                new ValuePayload(1),
                CancellationToken.None));
        Assert.True(stagingGate.WaitUntilBlocked(TimeSpan.FromSeconds(5)));

        try
        {
            Assert.Throws<IOException>(() =>
                Directory.Move(paths.JournalDirectory, paths.JournalDirectory + "-swapped"));
            var unrelatedPath = Path.Combine(root.Path, "unrelated.txt");
            await File.WriteAllTextAsync(unrelatedPath, "ordinary child writes remain available");
            Assert.True(File.Exists(unrelatedPath));
        }
        finally
        {
            stagingGate.Release();
            await write;
        }
    }

    [Fact]
    public async Task Public_read_waits_for_a_pinned_projection_publication_instead_of_failing_sharing()
    {
        using var root = new TemporaryDirectory();
        using var publicationGate = new BlockingPublisher();
        var paths = new WinoraDataPaths(root.Path);
        var destination = paths.ChangeIndexDocument;
        var initial = CreateFile(paths);
        await initial.WriteProjectionAsync(
            destination,
            new ValuePayload(1),
            CancellationToken.None);
        var writer = new AtomicJsonFile(
            paths,
            publisher: publicationGate,
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 13, 8, 30, 0, TimeSpan.Zero)));
        var reader = CreateFile(paths);

        var write = writer.WriteProjectionAsync(
            destination,
            new ValuePayload(2),
            CancellationToken.None).AsTask();
        Assert.True(publicationGate.WaitUntilBlocked(TimeSpan.FromSeconds(5)));

        var read = Task.Run(async () =>
            await reader.ReadProjectionAsync<ValuePayload>(
                destination,
                CancellationToken.None));
        bool readWasPending;
        try
        {
            await Task.Delay(100);
            readWasPending = !read.IsCompleted;
        }
        finally
        {
            publicationGate.Release();
        }

        await write;
        var result = await read;
        Assert.True(readWasPending);
        Assert.Equal(2, result.Document.Payload.Value);
    }

    [Fact]
    public async Task Published_handle_is_released_before_the_next_writer_enters_serialization()
    {
        using var root = new TemporaryDirectory();
        using var releaseGate = new BlockingPreparedHandleReleaseHook();
        var paths = new WinoraDataPaths(root.Path);
        var destination = paths.ChangeIndexDocument;
        var initial = CreateFile(paths);
        await initial.WriteProjectionAsync(
            destination,
            new ValuePayload(1),
            CancellationToken.None);
        var firstWriter = new AtomicJsonFile(
            paths,
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 13, 8, 30, 0, TimeSpan.Zero)),
            publicationRaceHook: releaseGate);
        var secondWriter = CreateFile(paths);

        var firstWrite = firstWriter.WriteProjectionAsync(
            destination,
            new ValuePayload(2),
            CancellationToken.None).AsTask();
        Assert.True(releaseGate.WaitUntilBlocked(TimeSpan.FromSeconds(5)));

        var secondWrite = secondWriter.WriteProjectionAsync(
            destination,
            new ValuePayload(3),
            CancellationToken.None).AsTask();
        bool secondWriteWasPending;
        try
        {
            await Task.Delay(100);
            secondWriteWasPending = !secondWrite.IsCompleted;
        }
        finally
        {
            releaseGate.Release();
        }

        await firstWrite;
        await secondWrite;
        Assert.True(secondWriteWasPending);
        var result = await secondWriter.ReadProjectionAsync<ValuePayload>(
            destination,
            CancellationToken.None);
        Assert.Equal(3, result.Document.Payload.Value);
    }

    [Fact]
    public async Task Transaction_read_uses_the_owned_serialization_scope_without_reentering_it()
    {
        using var root = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var destination = paths.ChangeIndexDocument;
        var file = CreateFile(paths);
        await file.WriteProjectionAsync(
            destination,
            new ValuePayload(7),
            CancellationToken.None);

        var read = file.ExecuteTransactionAsync(
            transaction => transaction.ReadProjection<ValuePayload>(destination),
            CancellationToken.None).AsTask();

        var result = await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7, result.Document.Payload.Value);
    }

    [Fact]
    public async Task Transaction_publish_releases_its_prepared_handle_before_unlocking()
    {
        using var root = new TemporaryDirectory();
        using var releaseGate = new BlockingPreparedHandleReleaseHook();
        var paths = new WinoraDataPaths(root.Path);
        var destination = paths.ChangeIndexDocument;
        var initial = CreateFile(paths);
        await initial.WriteProjectionAsync(
            destination,
            new ValuePayload(1),
            CancellationToken.None);
        var transactionOwner = new AtomicJsonFile(
            paths,
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 13, 8, 30, 0, TimeSpan.Zero)),
            publicationRaceHook: releaseGate);
        using var prepared = transactionOwner.PrepareProjection(
            destination,
            new ValuePayload(2),
            CancellationToken.None);

        var publish = transactionOwner.ExecuteTransactionAsync(
            transaction => transaction.ReplaceProjection(prepared),
            CancellationToken.None).AsTask();

        Assert.True(releaseGate.WaitUntilBlocked(TimeSpan.FromSeconds(5)));
        releaseGate.Release();
        var result = await publish;
        Assert.Equal(2, result.Payload.Value);
    }

    [Fact]
    public async Task Repairing_a_corrupt_projection_preserves_valid_lkg_and_removes_quarantine()
    {
        using var root = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var file = CreateFile(paths);
        await file.WriteProjectionAsync(
            paths.ChangeIndexDocument,
            new ValuePayload(1),
            CancellationToken.None);
        await file.WriteProjectionAsync(
            paths.ChangeIndexDocument,
            new ValuePayload(2),
            CancellationToken.None);
        await File.WriteAllTextAsync(paths.ChangeIndexDocument.FilePath, "corrupt");

        await file.WriteProjectionAsync(
            paths.ChangeIndexDocument,
            new ValuePayload(3),
            CancellationToken.None);

        var current = await file.ReadProjectionAsync<ValuePayload>(
            paths.ChangeIndexDocument,
            CancellationToken.None);
        Assert.Equal(ProjectionReadSource.Primary, current.Source);
        Assert.Equal(3, current.Document.Payload.Value);
        var lkg = new JsonDocumentSerializer().DeserializeAndValidate<ValuePayload>(
            await File.ReadAllBytesAsync(paths.ChangeIndexDocument.LastKnownGoodFilePath));
        Assert.Equal(1, lkg.Payload.Value);
        Assert.Empty(Directory.EnumerateFiles(paths.DataDirectory, "*.corrupt"));
        Assert.Empty(Directory.EnumerateFiles(paths.DataDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Preparation_failure_releases_the_pinned_path_chain()
    {
        using var root = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var file = CreateFile(paths);
        var payload = new CyclicPayload();
        payload.Next = payload;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            file.CreateNewAsync(
                paths.GetJournalEventDocument("event-id"),
                payload,
                CancellationToken.None).AsTask());

        Directory.Move(paths.JournalDirectory, paths.JournalDirectory + "-moved");
    }

    private static AtomicJsonFile CreateFile(WinoraDataPaths paths) =>
        new(
            paths,
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 13, 8, 30, 0, TimeSpan.Zero)));

    private static void CreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo(
            "cmd.exe",
            $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Unable to start junction helper.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }
}

internal sealed class CyclicPayload
{
    public CyclicPayload? Next { get; set; }
}

internal sealed class BlockingFlushDurability : IFileDurability, IDisposable
{
    private readonly WindowsFileDurability _inner = new();
    private readonly ManualResetEventSlim _blocked = new();
    private readonly ManualResetEventSlim _release = new();

    public void FlushToDisk(FileStream stream)
    {
        _blocked.Set();
        _release.Wait();
        _inner.FlushToDisk(stream);
    }

    internal bool WaitUntilBlocked(TimeSpan timeout) => _blocked.Wait(timeout);

    internal void Release() => _release.Set();

    public void Dispose()
    {
        _blocked.Dispose();
        _release.Dispose();
    }
}

internal sealed class BlockingPublisher : IWriteThroughPublisher, IDisposable
{
    private readonly WriteThroughPublisher _inner = new();
    private readonly ManualResetEventSlim _blocked = new();
    private readonly ManualResetEventSlim _release = new();

    public ValueTask PublishNewAsync(
        ValidatedFileHandle temporaryFile,
        string finalPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken)
    {
        Block();
        return _inner.PublishNewAsync(
            temporaryFile,
            finalPath,
            expectedHash,
            cancellationToken);
    }

    public ValueTask ReplaceProjectionAsync(
        ValidatedFileHandle temporaryFile,
        ValidatedFileHandle targetFile,
        string finalPath,
        ValidatedFileHandle? existingLastKnownGoodFile,
        string lastKnownGoodPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken)
    {
        Block();
        return _inner.ReplaceProjectionAsync(
            temporaryFile,
            targetFile,
            finalPath,
            existingLastKnownGoodFile,
            lastKnownGoodPath,
            expectedHash,
            cancellationToken);
    }

    internal bool WaitUntilBlocked(TimeSpan timeout) => _blocked.Wait(timeout);

    internal void Release() => _release.Set();

    private void Block()
    {
        _blocked.Set();
        _release.Wait();
    }

    public void Dispose()
    {
        _blocked.Dispose();
        _release.Dispose();
    }
}

internal sealed class BlockingPreparedHandleReleaseHook : IAtomicPublicationRaceHook, IDisposable
{
    private readonly ManualResetEventSlim _blocked = new();
    private readonly ManualResetEventSlim _release = new();

    public void AfterInitialIdentityValidation(AtomicPublicationContext context)
    {
    }

    public void BeforePreparedHandleRelease()
    {
        _blocked.Set();
        _release.Wait();
    }

    internal bool WaitUntilBlocked(TimeSpan timeout) => _blocked.Wait(timeout);

    internal void Release() => _release.Set();

    public void Dispose()
    {
        _blocked.Dispose();
        _release.Dispose();
    }
}

internal sealed class SignalingPublisher(
    TaskCompletionSource entered) : IWriteThroughPublisher
{
    private readonly WriteThroughPublisher _inner = new();

    public ValueTask PublishNewAsync(
        ValidatedFileHandle temporaryFile,
        string finalPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken)
    {
        entered.TrySetResult();
        return _inner.PublishNewAsync(
            temporaryFile,
            finalPath,
            expectedHash,
            cancellationToken);
    }

    public ValueTask ReplaceProjectionAsync(
        ValidatedFileHandle temporaryFile,
        ValidatedFileHandle targetFile,
        string finalPath,
        ValidatedFileHandle? existingLastKnownGoodFile,
        string lastKnownGoodPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken)
    {
        entered.TrySetResult();
        return _inner.ReplaceProjectionAsync(
            temporaryFile,
            targetFile,
            finalPath,
            existingLastKnownGoodFile,
            lastKnownGoodPath,
            expectedHash,
            cancellationToken);
    }
}
