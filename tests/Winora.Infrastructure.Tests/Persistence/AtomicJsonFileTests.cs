using System.Text;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;
using Xunit;

namespace Winora.Infrastructure.Tests.Persistence;

public sealed class AtomicJsonFileTests
{
    private static readonly DateTimeOffset CreatedUtc =
        new(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Authoritative_create_is_readable_only_after_verified_publication()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path);
        var path = fixture.Paths.GetJournalEventFile("event-id");

        var acknowledgement = await fixture.File.CreateNewAsync(
            path,
            "event-id",
            new ValuePayload(42),
            CancellationToken.None);
        var readback = await fixture.File.ReadAsync<ValuePayload>(path, CancellationToken.None);

        Assert.Equal("event-id", acknowledgement.DocumentId);
        Assert.Equal(CreatedUtc, acknowledgement.CreatedUtc);
        Assert.Equal(42, readback.Payload.Value);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task Authoritative_create_never_overwrites_an_existing_event()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path);
        var path = fixture.Paths.GetJournalEventFile("event-id");
        await fixture.File.CreateNewAsync(
            path,
            "event-id",
            new ValuePayload(1),
            CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.File.CreateNewAsync(
                path,
                "event-id",
                new ValuePayload(2),
                CancellationToken.None).AsTask());

        Assert.Equal(1, (await fixture.File.ReadAsync<ValuePayload>(path, CancellationToken.None)).Payload.Value);
    }

    [Fact]
    public async Task Projection_replace_keeps_the_previous_document_as_last_known_good()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path);
        var path = fixture.Paths.ChangeIndexFile;
        await fixture.File.WriteProjectionAsync(
            path,
            "change-index",
            new ValuePayload(1),
            CancellationToken.None);

        await fixture.File.WriteProjectionAsync(
            path,
            "change-index",
            new ValuePayload(2),
            CancellationToken.None);

        Assert.Equal(2, (await fixture.File.ReadAsync<ValuePayload>(path, CancellationToken.None)).Payload.Value);
        var lastKnownGood = await fixture.File.ReadAsync<ValuePayload>(
            path + ".last-known-good",
            CancellationToken.None);
        Assert.Equal(1, lastKnownGood.Payload.Value);
    }

    [Fact]
    public async Task Temp_flush_failure_never_replaces_the_last_known_good_document()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path);
        var path = fixture.Paths.ChangeIndexFile;
        await fixture.File.WriteProjectionAsync(
            path,
            "change-index",
            new ValuePayload(1),
            CancellationToken.None);
        var failing = CreateFixture(
            directory.Path,
            durability: new ThrowOnFlushDurability());

        await Assert.ThrowsAsync<InjectedStorageException>(() =>
            failing.File.WriteProjectionAsync(
                path,
                "change-index",
                new ValuePayload(2),
                CancellationToken.None).AsTask());

        Assert.Equal(1, (await fixture.File.ReadAsync<ValuePayload>(path, CancellationToken.None)).Payload.Value);
    }

    [Fact]
    public async Task Temp_readback_corruption_is_rejected_before_publication()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(
            directory.Path,
            validatedFileObserver: new CorruptValidatedReadObserver(
                ValidatedFileUse.StagingReadback));
        var path = fixture.Paths.GetJournalEventFile("event-id");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.File.CreateNewAsync(
                path,
                "event-id",
                new ValuePayload(1),
                CancellationToken.None).AsTask());

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Before_publish_failure_preserves_target_and_uses_unique_same_directory_staging()
    {
        using var directory = new TemporaryDirectory();
        var baseline = CreateFixture(directory.Path);
        var path = baseline.Paths.ChangeIndexFile;
        await baseline.File.WriteProjectionAsync(
            path,
            "change-index",
            new ValuePayload(1),
            CancellationToken.None);
        var publisher = new ThrowBeforePublishPublisher();
        var failing = CreateFixture(directory.Path, publisher: publisher);

        for (var value = 2; value <= 3; value++)
        {
            await Assert.ThrowsAsync<InjectedStorageException>(() =>
                failing.File.WriteProjectionAsync(
                    path,
                    "change-index",
                    new ValuePayload(value),
                    CancellationToken.None).AsTask());
        }

        Assert.Equal(2, publisher.TemporaryPaths.Count);
        Assert.Equal(2, publisher.TemporaryPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(
            publisher.TemporaryPaths,
            temporaryPath => Assert.Equal(
                Path.GetDirectoryName(path),
                Path.GetDirectoryName(temporaryPath),
                ignoreCase: true));
        Assert.All(publisher.TemporaryPaths, temporaryPath => Assert.False(File.Exists(temporaryPath)));
        Assert.Equal(1, (await baseline.File.ReadAsync<ValuePayload>(path, CancellationToken.None)).Payload.Value);
    }

    [Fact]
    public async Task Post_publication_readback_corruption_is_not_acknowledged_and_falls_back()
    {
        using var directory = new TemporaryDirectory();
        var baseline = CreateFixture(directory.Path);
        var path = baseline.Paths.ChangeIndexFile;
        await baseline.File.WriteProjectionAsync(
            path,
            "change-index",
            new ValuePayload(1),
            CancellationToken.None);
        var failing = CreateFixture(
            directory.Path,
            validatedFileObserver: new CorruptValidatedReadObserver(
                ValidatedFileUse.PostPublication));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            failing.File.WriteProjectionAsync(
                path,
                "change-index",
                new ValuePayload(2),
                CancellationToken.None).AsTask());

        await File.WriteAllTextAsync(path, "corrupt");
        Assert.Equal(1, (await baseline.File.ReadAsync<ValuePayload>(path, CancellationToken.None)).Payload.Value);
    }

    [Fact]
    public async Task Missing_projection_target_reads_the_last_known_good_document()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path);
        var path = fixture.Paths.ChangeIndexFile;
        await fixture.File.WriteProjectionAsync(
            path,
            "change-index",
            new ValuePayload(1),
            CancellationToken.None);
        await fixture.File.WriteProjectionAsync(
            path,
            "change-index",
            new ValuePayload(2),
            CancellationToken.None);
        File.Delete(path);

        var recovered = await fixture.File.ReadAsync<ValuePayload>(path, CancellationToken.None);

        Assert.Equal(1, recovered.Payload.Value);
    }

    [Fact]
    public async Task Hash_corrupt_projection_target_reads_the_last_known_good_document()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path);
        var path = fixture.Paths.ChangeIndexFile;
        await fixture.File.WriteProjectionAsync(
            path,
            "change-index",
            new ValuePayload(1),
            CancellationToken.None);
        await fixture.File.WriteProjectionAsync(
            path,
            "change-index",
            new ValuePayload(2),
            CancellationToken.None);
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace(":2", ":9", StringComparison.Ordinal));

        var recovered = await fixture.File.ReadAsync<ValuePayload>(path, CancellationToken.None);

        Assert.Equal(1, recovered.Payload.Value);
    }

    [Fact]
    public async Task Schema_corrupt_authoritative_document_is_rejected()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path);
        var path = fixture.Paths.GetJournalEventFile("event-id");
        await fixture.File.CreateNewAsync(
            path,
            "event-id",
            new ValuePayload(1),
            CancellationToken.None);
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace(
            "\"schemaVersion\":1",
            "\"schemaVersion\":2",
            StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.File.ReadAsync<ValuePayload>(path, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Projection_stable_identifier_cannot_change_during_replace()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path);
        var path = fixture.Paths.ChangeIndexFile;
        await fixture.File.WriteProjectionAsync(
            path,
            "change-index",
            new ValuePayload(1),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.File.WriteProjectionAsync(
                path,
                "different-id",
                new ValuePayload(2),
                CancellationToken.None).AsTask());

        Assert.Equal(1, (await fixture.File.ReadAsync<ValuePayload>(path, CancellationToken.None)).Payload.Value);
    }

    [Fact]
    public async Task Concurrent_local_writers_are_serialized_and_leave_a_valid_projection()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var path = paths.ChangeIndexFile;
        var publisher = new TrackingPublisher(new WriteThroughPublisher());
        var initial = CreateFixture(directory.Path, publisher: publisher);
        await initial.File.WriteProjectionAsync(
            path,
            "change-index",
            new ValuePayload(0),
            CancellationToken.None);

        var writes = Enumerable.Range(1, 12).Select(value =>
            CreateFixture(directory.Path, publisher: publisher).File.WriteProjectionAsync(
                path,
                "change-index",
                new ValuePayload(value),
                CancellationToken.None).AsTask());
        await Task.WhenAll(writes);

        var result = await initial.File.ReadAsync<ValuePayload>(path, CancellationToken.None);
        Assert.InRange(result.Payload.Value, 1, 12);
        Assert.Equal(1, publisher.MaximumConcurrency);
        Assert.Empty(Directory.EnumerateFiles(paths.DataDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Atomic_file_rejects_a_destination_outside_the_fixed_root()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path);
        var outside = Path.Combine(
            Path.GetDirectoryName(directory.Path)!,
            Guid.NewGuid().ToString("N"),
            "event.json");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.File.CreateNewAsync(
                outside,
                "event-id",
                new ValuePayload(1),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Cancellation_after_durable_publication_does_not_report_the_committed_event_as_failed()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var paths = new WinoraDataPaths(directory.Path);
        var publisher = new CancelAfterPublishingPublisher(
            new WriteThroughPublisher(),
            cancellation);
        var file = new AtomicJsonFile(
            paths,
            publisher,
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(CreatedUtc));
        var destination = paths.GetJournalEventDocument("durable-event");

        var committed = await file.CreateNewAsync(
            destination,
            new ValuePayload(7),
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(7, committed.Payload.Value);
        Assert.Equal(
            7,
            (await file.ReadAuthoritativeAsync<ValuePayload>(
                destination,
                CancellationToken.None)).Payload.Value);
    }

    private static AtomicFixture CreateFixture(
        string root,
        IWriteThroughPublisher? publisher = null,
        IFileDurability? durability = null,
        IValidatedFileObserver? validatedFileObserver = null)
    {
        var paths = new WinoraDataPaths(root);
        var file = new AtomicJsonFile(
            paths,
            publisher,
            durability,
            new JsonDocumentSerializer(),
            new FixedTimeProvider(CreatedUtc),
            validatedFileObserver);
        return new AtomicFixture(paths, file);
    }

    private sealed record AtomicFixture(WinoraDataPaths Paths, AtomicJsonFile File);
}

internal sealed class ThrowOnFlushDurability : IFileDurability
{
    public void FlushToDisk(FileStream stream) => throw new InjectedStorageException();
}

internal sealed class CorruptValidatedReadObserver(
    ValidatedFileUse targetUse) : IValidatedFileObserver
{
    public void OnValidated(
        string path,
        ValidatedFileIdentity identity,
        ValidatedFileUse use)
    {
    }

    public byte[] TransformRead(
        string path,
        ValidatedFileIdentity identity,
        ValidatedFileUse use,
        byte[] bytes) =>
        use == targetUse
            ? Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"payload\":{}}")
            : bytes;
}

internal sealed class ThrowBeforePublishPublisher : IWriteThroughPublisher
{
    internal List<string> TemporaryPaths { get; } = [];

    public ValueTask PublishNewAsync(
        ValidatedFileHandle temporaryFile,
        string finalPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken)
    {
        TemporaryPaths.Add(temporaryFile.Path);
        throw new InjectedStorageException();
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
        TemporaryPaths.Add(temporaryFile.Path);
        throw new InjectedStorageException();
    }
}

internal sealed class TrackingPublisher(IWriteThroughPublisher inner) : IWriteThroughPublisher
{
    private int _concurrency;
    private int _maximumConcurrency;

    internal int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

    public async ValueTask PublishNewAsync(
        ValidatedFileHandle temporaryFile,
        string finalPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            await Task.Delay(10, cancellationToken);
            await inner.PublishNewAsync(
                temporaryFile,
                finalPath,
                expectedHash,
                cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _concurrency);
        }
    }

    public async ValueTask ReplaceProjectionAsync(
        ValidatedFileHandle temporaryFile,
        ValidatedFileHandle targetFile,
        string finalPath,
        ValidatedFileHandle? existingLastKnownGoodFile,
        string lastKnownGoodPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            await Task.Delay(10, cancellationToken);
            await inner.ReplaceProjectionAsync(
                temporaryFile,
                targetFile,
                finalPath,
                existingLastKnownGoodFile,
                lastKnownGoodPath,
                expectedHash,
                cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _concurrency);
        }
    }

    private void Enter()
    {
        var current = Interlocked.Increment(ref _concurrency);
        var observed = Volatile.Read(ref _maximumConcurrency);
        while (current > observed)
        {
            observed = Interlocked.CompareExchange(ref _maximumConcurrency, current, observed);
        }
    }
}

internal sealed class CancelAfterPublishingPublisher(
    IWriteThroughPublisher inner,
    CancellationTokenSource cancellation) : IWriteThroughPublisher
{
    public async ValueTask PublishNewAsync(
        ValidatedFileHandle temporaryFile,
        string finalPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken)
    {
        await inner.PublishNewAsync(
            temporaryFile,
            finalPath,
            expectedHash,
            cancellationToken);
        cancellation.Cancel();
    }

    public async ValueTask ReplaceProjectionAsync(
        ValidatedFileHandle temporaryFile,
        ValidatedFileHandle targetFile,
        string finalPath,
        ValidatedFileHandle? existingLastKnownGoodFile,
        string lastKnownGoodPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken)
    {
        await inner.ReplaceProjectionAsync(
            temporaryFile,
            targetFile,
            finalPath,
            existingLastKnownGoodFile,
            lastKnownGoodPath,
            expectedHash,
            cancellationToken);
        cancellation.Cancel();
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
