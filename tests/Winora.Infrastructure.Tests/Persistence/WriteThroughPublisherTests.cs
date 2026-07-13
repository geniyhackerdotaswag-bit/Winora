using Winora.Infrastructure.Persistence;
using Xunit;

namespace Winora.Infrastructure.Tests.Persistence;

public sealed class WriteThroughPublisherTests
{
    [Fact]
    public async Task Authoritative_publication_moves_the_staged_file_without_overwrite()
    {
        using var directory = new TemporaryDirectory();
        var temporaryPath = directory.File("event.tmp");
        var finalPath = directory.File("event.json");
        await File.WriteAllTextAsync(temporaryPath, "durable-event");
        var publisher = new WriteThroughPublisher();

        await publisher.PublishNewAsync(temporaryPath, finalPath, CancellationToken.None);

        Assert.False(File.Exists(temporaryPath));
        Assert.Equal("durable-event", await File.ReadAllTextAsync(finalPath));

        await File.WriteAllTextAsync(temporaryPath, "replacement");
        await Assert.ThrowsAsync<IOException>(() =>
            publisher.PublishNewAsync(temporaryPath, finalPath, CancellationToken.None).AsTask());
        Assert.Equal("durable-event", await File.ReadAllTextAsync(finalPath));
    }

    [Fact]
    public async Task Projection_replacement_retains_the_original_as_last_known_good()
    {
        using var directory = new TemporaryDirectory();
        var temporaryPath = directory.File("index.tmp");
        var finalPath = directory.File("index.json");
        var lastKnownGoodPath = directory.File("index.json.last-known-good");
        await File.WriteAllTextAsync(temporaryPath, "new-index");
        await File.WriteAllTextAsync(finalPath, "old-index");
        var publisher = new WriteThroughPublisher();

        await publisher.ReplaceProjectionAsync(
            temporaryPath,
            finalPath,
            lastKnownGoodPath,
            CancellationToken.None);

        Assert.Equal("new-index", await File.ReadAllTextAsync(finalPath));
        Assert.Equal("old-index", await File.ReadAllTextAsync(lastKnownGoodPath));
    }

    [Fact]
    public async Task Publication_is_not_acknowledged_when_reopened_bytes_differ()
    {
        using var directory = new TemporaryDirectory();
        var temporaryPath = directory.File("event.tmp");
        var finalPath = directory.File("event.json");
        await File.WriteAllTextAsync(temporaryPath, "expected");
        var operations = new WindowsAtomicFileOperations();
        var durability = new CorruptBeforeReopenDurability(finalPath);
        var publisher = new WriteThroughPublisher(operations, durability);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            publisher.PublishNewAsync(temporaryPath, finalPath, CancellationToken.None).AsTask());

        Assert.True(durability.ReopenAttempted);
        Assert.Equal("corrupt", await File.ReadAllTextAsync(finalPath));
    }

    [Fact]
    public async Task Write_through_move_failure_does_not_publish_or_acknowledge()
    {
        using var directory = new TemporaryDirectory();
        var temporaryPath = directory.File("event.tmp");
        var finalPath = directory.File("event.json");
        await File.WriteAllTextAsync(temporaryPath, "expected");
        var publisher = new WriteThroughPublisher(
            new ThrowingAtomicFileOperations(throwOnMove: true),
            new WindowsFileDurability());

        await Assert.ThrowsAsync<InjectedStorageException>(() =>
            publisher.PublishNewAsync(temporaryPath, finalPath, CancellationToken.None).AsTask());

        Assert.True(File.Exists(temporaryPath));
        Assert.False(File.Exists(finalPath));
    }

    [Fact]
    public async Task Projection_replace_failure_preserves_the_published_target()
    {
        using var directory = new TemporaryDirectory();
        var temporaryPath = directory.File("index.tmp");
        var finalPath = directory.File("index.json");
        var lastKnownGoodPath = directory.File("index.json.last-known-good");
        await File.WriteAllTextAsync(temporaryPath, "new-index");
        await File.WriteAllTextAsync(finalPath, "old-index");
        var publisher = new WriteThroughPublisher(
            new ThrowingAtomicFileOperations(throwOnReplace: true),
            new WindowsFileDurability());

        await Assert.ThrowsAsync<InjectedStorageException>(() =>
            publisher.ReplaceProjectionAsync(
                temporaryPath,
                finalPath,
                lastKnownGoodPath,
                CancellationToken.None).AsTask());

        Assert.Equal("old-index", await File.ReadAllTextAsync(finalPath));
        Assert.False(File.Exists(lastKnownGoodPath));
    }
}

internal sealed class CorruptBeforeReopenDurability(string pathToCorrupt) : IFileDurability
{
    private readonly WindowsFileDurability _inner = new();

    internal bool ReopenAttempted { get; private set; }

    public void FlushToDisk(FileStream stream) => _inner.FlushToDisk(stream);

    public byte[] ReopenReadAndFlush(string path)
    {
        ReopenAttempted = true;
        if (StringComparer.OrdinalIgnoreCase.Equals(path, pathToCorrupt))
        {
            File.WriteAllText(path, "corrupt");
        }

        return _inner.ReopenReadAndFlush(path);
    }
}

internal sealed class ThrowingAtomicFileOperations(
    bool throwOnMove = false,
    bool throwOnReplace = false) : IAtomicFileOperations
{
    public void MoveNewFileWriteThrough(string temporaryPath, string finalPath)
    {
        if (throwOnMove)
        {
            throw new InjectedStorageException();
        }

        new WindowsAtomicFileOperations().MoveNewFileWriteThrough(temporaryPath, finalPath);
    }

    public void ReplaceFile(string temporaryPath, string finalPath, string lastKnownGoodPath)
    {
        if (throwOnReplace)
        {
            throw new InjectedStorageException();
        }

        new WindowsAtomicFileOperations().ReplaceFile(temporaryPath, finalPath, lastKnownGoodPath);
    }
}

internal sealed class InjectedStorageException : Exception
{
}

internal sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Winora.Infrastructure.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
