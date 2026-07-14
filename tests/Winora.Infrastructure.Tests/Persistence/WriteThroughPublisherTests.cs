using System.Security.Cryptography;
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

        using (var temporaryFile = OpenForPublication(temporaryPath))
        {
            await publisher.PublishNewAsync(
                temporaryFile,
                finalPath,
                Hash("durable-event"),
                CancellationToken.None);
        }

        Assert.False(File.Exists(temporaryPath));
        Assert.Equal("durable-event", await File.ReadAllTextAsync(finalPath));

        await File.WriteAllTextAsync(temporaryPath, "replacement");
        using (var replacementFile = OpenForPublication(temporaryPath))
        {
            await Assert.ThrowsAsync<IOException>(() =>
                publisher.PublishNewAsync(
                    replacementFile,
                    finalPath,
                    Hash("replacement"),
                    CancellationToken.None).AsTask());
        }
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

        using (var temporaryFile = OpenForPublication(temporaryPath))
        using (var targetFile = OpenForPublication(finalPath))
        {
            await publisher.ReplaceProjectionAsync(
                temporaryFile,
                targetFile,
                finalPath,
                existingLastKnownGoodFile: null,
                lastKnownGoodPath,
                Hash("new-index"),
                CancellationToken.None);
        }

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
        var observer = new CorruptValidatedReadObserver(ValidatedFileUse.PostPublication);
        var publisher = new WriteThroughPublisher(operations);

        using (var temporaryFile = new WindowsValidatedFileAccess(observer).OpenForMutation(
                   temporaryPath,
                   ValidatedFileUse.PostPublication))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                publisher.PublishNewAsync(
                    temporaryFile,
                    finalPath,
                    Hash("expected"),
                    CancellationToken.None).AsTask());
        }

        Assert.False(File.Exists(finalPath));
    }

    [Fact]
    public async Task Write_through_move_failure_does_not_publish_or_acknowledge()
    {
        using var directory = new TemporaryDirectory();
        var temporaryPath = directory.File("event.tmp");
        var finalPath = directory.File("event.json");
        await File.WriteAllTextAsync(temporaryPath, "expected");
        var publisher = new WriteThroughPublisher(
            new ThrowingAtomicFileOperations(throwOnMove: true));

        using (var temporaryFile = OpenForPublication(temporaryPath))
        {
            await Assert.ThrowsAsync<InjectedStorageException>(() =>
                publisher.PublishNewAsync(
                    temporaryFile,
                    finalPath,
                    Hash("expected"),
                    CancellationToken.None).AsTask());
        }

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
            new ThrowingAtomicFileOperations(throwOnReplace: true));

        using (var temporaryFile = OpenForPublication(temporaryPath))
        using (var targetFile = OpenForPublication(finalPath))
        {
            await Assert.ThrowsAsync<InjectedStorageException>(() =>
                publisher.ReplaceProjectionAsync(
                    temporaryFile,
                    targetFile,
                    finalPath,
                    existingLastKnownGoodFile: null,
                    lastKnownGoodPath,
                    Hash("new-index"),
                    CancellationToken.None).AsTask());
        }

        Assert.Equal("old-index", await File.ReadAllTextAsync(finalPath));
        Assert.False(File.Exists(lastKnownGoodPath));
    }

    [Fact]
    public async Task Authoritative_publication_does_not_publish_a_staging_file_swapped_after_last_validation()
    {
        using var directory = new TemporaryDirectory();
        var temporaryPath = directory.File("event.tmp");
        var finalPath = directory.File("event.json");
        await File.WriteAllTextAsync(temporaryPath, "validated-event");
        var operations = new SwapImmediatelyBeforeMutationOperations(
            temporaryPath,
            "external-event");
        var publisher = new WriteThroughPublisher(operations);

        using (var temporaryFile = OpenForPublication(temporaryPath))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                publisher.PublishNewAsync(
                    temporaryFile,
                    finalPath,
                    Hash("validated-event"),
                    CancellationToken.None).AsTask());
        }

        Assert.False(File.Exists(finalPath));
        Assert.Equal("validated-event", await File.ReadAllTextAsync(temporaryPath));
    }

    [Theory]
    [InlineData(PublicationSwapTarget.Staging)]
    [InlineData(PublicationSwapTarget.Final)]
    [InlineData(PublicationSwapTarget.Backup)]
    public async Task Projection_replacement_does_not_overwrite_a_leaf_swapped_after_last_validation(
        PublicationSwapTarget target)
    {
        using var directory = new TemporaryDirectory();
        var temporaryPath = directory.File("index.tmp");
        var finalPath = directory.File("index.json");
        var lastKnownGoodPath = directory.File("index.json.last-known-good");
        await File.WriteAllTextAsync(temporaryPath, "validated-new-index");
        await File.WriteAllTextAsync(finalPath, "validated-old-index");
        await File.WriteAllTextAsync(lastKnownGoodPath, "validated-older-index");
        var attackedPath = target switch
        {
            PublicationSwapTarget.Staging => temporaryPath,
            PublicationSwapTarget.Final => finalPath,
            PublicationSwapTarget.Backup => lastKnownGoodPath,
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        var operations = new SwapImmediatelyBeforeMutationOperations(
            attackedPath,
            "external-leaf");
        var publisher = new WriteThroughPublisher(operations);

        using (var temporaryFile = OpenForPublication(temporaryPath))
        using (var targetFile = OpenForPublication(finalPath))
        using (var lastKnownGoodFile = OpenForPublication(lastKnownGoodPath))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                publisher.ReplaceProjectionAsync(
                    temporaryFile,
                    targetFile,
                    finalPath,
                    lastKnownGoodFile,
                    lastKnownGoodPath,
                    Hash("validated-new-index"),
                    CancellationToken.None).AsTask());
        }

        Assert.Equal("validated-new-index", await File.ReadAllTextAsync(temporaryPath));
        Assert.Equal("validated-old-index", await File.ReadAllTextAsync(finalPath));
        Assert.Equal("validated-older-index", await File.ReadAllTextAsync(lastKnownGoodPath));
    }

    [Fact]
    public async Task Projection_replacement_flushes_each_successful_handle_bound_rename()
    {
        using var directory = new TemporaryDirectory();
        var temporaryPath = directory.File("index.tmp");
        var finalPath = directory.File("index.json");
        var lastKnownGoodPath = directory.File("index.json.last-known-good");
        await File.WriteAllTextAsync(temporaryPath, "new-index");
        await File.WriteAllTextAsync(finalPath, "old-index");
        var durability = new TrackingHandleDurability();
        var publisher = new WriteThroughPublisher(
            new WindowsAtomicFileOperations(),
            durability);

        using (var temporaryFile = OpenForPublication(temporaryPath))
        using (var targetFile = OpenForPublication(finalPath))
        {
            await publisher.ReplaceProjectionAsync(
                temporaryFile,
                targetFile,
                finalPath,
                existingLastKnownGoodFile: null,
                lastKnownGoodPath,
                Hash("new-index"),
                CancellationToken.None);
        }

        Assert.Equal([finalPath, temporaryPath], durability.FlushedSourcePaths);
    }

    [Fact]
    public async Task Existing_last_known_good_is_retained_until_the_new_target_passes_readback()
    {
        using var directory = new TemporaryDirectory();
        var temporaryPath = directory.File("index.tmp");
        var finalPath = directory.File("index.json");
        var lastKnownGoodPath = directory.File("index.json.last-known-good");
        await File.WriteAllTextAsync(temporaryPath, "new-index");
        await File.WriteAllTextAsync(finalPath, "old-index");
        await File.WriteAllTextAsync(lastKnownGoodPath, "older-index");
        var operations = new TrackingAtomicFileOperations();
        var publisher = new WriteThroughPublisher(operations);

        using (var temporaryFile = OpenForPublication(temporaryPath))
        using (var targetFile = OpenForPublication(finalPath))
        using (var lastKnownGoodFile = OpenForPublication(lastKnownGoodPath))
        {
            await publisher.ReplaceProjectionAsync(
                temporaryFile,
                targetFile,
                finalPath,
                lastKnownGoodFile,
                lastKnownGoodPath,
                Hash("new-index"),
                CancellationToken.None);
        }

        Assert.Equal(
            ["Rename:index.json.last-known-good", "Rename:index.json", "Rename:index.tmp", "Delete:index.json.last-known-good"],
            operations.Events);
        Assert.Equal("new-index", await File.ReadAllTextAsync(finalPath));
        Assert.Equal("old-index", await File.ReadAllTextAsync(lastKnownGoodPath));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.retained.*"));
    }

    [Fact]
    public async Task Failure_after_retaining_existing_last_known_good_restores_its_canonical_name()
    {
        using var directory = new TemporaryDirectory();
        var temporaryPath = directory.File("index.tmp");
        var finalPath = directory.File("index.json");
        var lastKnownGoodPath = directory.File("index.json.last-known-good");
        await File.WriteAllTextAsync(temporaryPath, "new-index");
        await File.WriteAllTextAsync(finalPath, "old-index");
        await File.WriteAllTextAsync(lastKnownGoodPath, "older-index");
        var publisher = new WriteThroughPublisher(
            new FailOnRenameNumberOperations(failingRename: 2));

        using (var temporaryFile = OpenForPublication(temporaryPath))
        using (var targetFile = OpenForPublication(finalPath))
        using (var lastKnownGoodFile = OpenForPublication(lastKnownGoodPath))
        {
            await Assert.ThrowsAsync<InjectedStorageException>(() =>
                publisher.ReplaceProjectionAsync(
                    temporaryFile,
                    targetFile,
                    finalPath,
                    lastKnownGoodFile,
                    lastKnownGoodPath,
                    Hash("new-index"),
                    CancellationToken.None).AsTask());
        }

        Assert.Equal("old-index", await File.ReadAllTextAsync(finalPath));
        Assert.Equal("older-index", await File.ReadAllTextAsync(lastKnownGoodPath));
        Assert.Equal("new-index", await File.ReadAllTextAsync(temporaryPath));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.retained.*"));
    }

    [Fact]
    public async Task Failure_between_projection_renames_restores_the_exact_original_target()
    {
        using var directory = new TemporaryDirectory();
        var temporaryPath = directory.File("index.tmp");
        var finalPath = directory.File("index.json");
        var lastKnownGoodPath = directory.File("index.json.last-known-good");
        await File.WriteAllTextAsync(temporaryPath, "new-index");
        await File.WriteAllTextAsync(finalPath, "old-index");
        var publisher = new WriteThroughPublisher(
            new FailOnRenameNumberOperations(failingRename: 2));

        using (var temporaryFile = OpenForPublication(temporaryPath))
        using (var targetFile = OpenForPublication(finalPath))
        {
            await Assert.ThrowsAsync<InjectedStorageException>(() =>
                publisher.ReplaceProjectionAsync(
                    temporaryFile,
                    targetFile,
                    finalPath,
                    existingLastKnownGoodFile: null,
                    lastKnownGoodPath,
                    Hash("new-index"),
                    CancellationToken.None).AsTask());
        }

        Assert.Equal("old-index", await File.ReadAllTextAsync(finalPath));
        Assert.Equal("new-index", await File.ReadAllTextAsync(temporaryPath));
        Assert.False(File.Exists(lastKnownGoodPath));
    }

    [Fact]
    public async Task Compensation_never_overwrites_an_external_target_created_between_renames()
    {
        using var directory = new TemporaryDirectory();
        var temporaryPath = directory.File("index.tmp");
        var finalPath = directory.File("index.json");
        var lastKnownGoodPath = directory.File("index.json.last-known-good");
        await File.WriteAllTextAsync(temporaryPath, "new-index");
        await File.WriteAllTextAsync(finalPath, "old-index");
        var publisher = new WriteThroughPublisher(
            new ExternalTargetOnSecondRenameOperations("external-index"));

        using (var temporaryFile = OpenForPublication(temporaryPath))
        using (var targetFile = OpenForPublication(finalPath))
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() =>
                publisher.ReplaceProjectionAsync(
                    temporaryFile,
                    targetFile,
                    finalPath,
                    existingLastKnownGoodFile: null,
                    lastKnownGoodPath,
                    Hash("new-index"),
                    CancellationToken.None).AsTask());

            Assert.Equal(2, failure.InnerExceptions.Count);
        }

        Assert.Equal("external-index", await File.ReadAllTextAsync(finalPath));
        Assert.Equal("old-index", await File.ReadAllTextAsync(lastKnownGoodPath));
        Assert.Equal("new-index", await File.ReadAllTextAsync(temporaryPath));
    }

    private static ValidatedFileHandle OpenForPublication(string path) =>
        new WindowsValidatedFileAccess().OpenForMutation(
            path,
            ValidatedFileUse.PrePublication);

    private static byte[] Hash(string value) =>
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
}

internal sealed class TrackingHandleDurability : IHandleDurability
{
    internal List<string> FlushedSourcePaths { get; } = [];

    public void FlushToDisk(ValidatedFileHandle file)
    {
        FlushedSourcePaths.Add(file.Path);
        file.FlushToDisk();
    }
}

internal sealed class TrackingAtomicFileOperations : IAtomicFileOperations
{
    private readonly WindowsAtomicFileOperations _inner = new();

    internal List<string> Events { get; } = [];

    public void RenameNoReplace(ValidatedFileHandle sourceFile, string destinationPath)
    {
        Events.Add($"Rename:{Path.GetFileName(sourceFile.Path)}");
        _inner.RenameNoReplace(sourceFile, destinationPath);
    }

    public void Delete(ValidatedFileHandle file)
    {
        Events.Add($"Delete:{Path.GetFileName(file.Path)}");
        _inner.Delete(file);
    }
}

internal sealed class ThrowingAtomicFileOperations(
    bool throwOnMove = false,
    bool throwOnReplace = false) : IAtomicFileOperations
{
    public void RenameNoReplace(ValidatedFileHandle sourceFile, string destinationPath)
    {
        if (throwOnMove || throwOnReplace)
        {
            throw new InjectedStorageException();
        }

        new WindowsAtomicFileOperations().RenameNoReplace(sourceFile, destinationPath);
    }

    public void Delete(ValidatedFileHandle file)
    {
        if (throwOnReplace)
        {
            throw new InjectedStorageException();
        }

        new WindowsAtomicFileOperations().Delete(file);
    }
}

internal sealed class SwapImmediatelyBeforeMutationOperations(
    string attackPath,
    string externalContents) : IAtomicFileOperations
{
    private readonly WindowsAtomicFileOperations _inner = new();
    private bool _attackAttempted;

    public void RenameNoReplace(ValidatedFileHandle sourceFile, string destinationPath)
    {
        AttackOnce();
        _inner.RenameNoReplace(sourceFile, destinationPath);
    }

    public void Delete(ValidatedFileHandle file)
    {
        AttackOnce();
        _inner.Delete(file);
    }

    private void AttackOnce()
    {
        if (_attackAttempted)
        {
            return;
        }

        _attackAttempted = true;
        File.Move(attackPath, attackPath + ".validated-original");
        File.WriteAllText(attackPath, externalContents);
    }
}

internal sealed class FailOnRenameNumberOperations(int failingRename) : IAtomicFileOperations
{
    private readonly WindowsAtomicFileOperations _inner = new();
    private int _renameCount;

    public void RenameNoReplace(ValidatedFileHandle sourceFile, string destinationPath)
    {
        if (Interlocked.Increment(ref _renameCount) == failingRename)
        {
            throw new InjectedStorageException();
        }

        _inner.RenameNoReplace(sourceFile, destinationPath);
    }

    public void Delete(ValidatedFileHandle file) => _inner.Delete(file);
}

internal sealed class ExternalTargetOnSecondRenameOperations(
    string externalContents) : IAtomicFileOperations
{
    private readonly WindowsAtomicFileOperations _inner = new();
    private int _renameCount;

    public void RenameNoReplace(ValidatedFileHandle sourceFile, string destinationPath)
    {
        if (Interlocked.Increment(ref _renameCount) == 2)
        {
            File.WriteAllText(destinationPath, externalContents);
        }

        _inner.RenameNoReplace(sourceFile, destinationPath);
    }

    public void Delete(ValidatedFileHandle file) => _inner.Delete(file);
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
