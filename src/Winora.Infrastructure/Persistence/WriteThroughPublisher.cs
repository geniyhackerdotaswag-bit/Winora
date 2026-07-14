using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace Winora.Infrastructure.Persistence;

internal interface IWriteThroughPublisher
{
    ValueTask PublishNewAsync(
        ValidatedFileHandle temporaryFile,
        string finalPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken);

    ValueTask ReplaceProjectionAsync(
        ValidatedFileHandle temporaryFile,
        ValidatedFileHandle targetFile,
        string finalPath,
        ValidatedFileHandle? existingLastKnownGoodFile,
        string lastKnownGoodPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken);
}

internal interface IAtomicFileOperations
{
    void RenameNoReplace(ValidatedFileHandle sourceFile, string destinationPath);

    void Delete(ValidatedFileHandle file);
}

internal interface IFileDurability
{
    void FlushToDisk(FileStream stream);
}

internal interface IHandleDurability
{
    void FlushToDisk(ValidatedFileHandle file);
}

internal sealed class WindowsFileDurability : IFileDurability
{
    public void FlushToDisk(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Microsoft Learn: https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-10.0
        stream.Flush(flushToDisk: true);
    }

}

internal sealed class WindowsAtomicFileOperations : IAtomicFileOperations
{
    public void RenameNoReplace(ValidatedFileHandle sourceFile, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(sourceFile);
        ValidateSameVolume(sourceFile.Path, destinationPath);
        sourceFile.RenameNoReplace(destinationPath);
    }

    public void Delete(ValidatedFileHandle file) =>
        (file ?? throw new ArgumentNullException(nameof(file))).MarkDelete();

    private static void ValidateSameVolume(string firstPath, string secondPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondPath);

        var firstRoot = Path.GetPathRoot(Path.GetFullPath(firstPath));
        var secondRoot = Path.GetPathRoot(Path.GetFullPath(secondPath));
        if (string.IsNullOrEmpty(firstRoot) ||
            !StringComparer.OrdinalIgnoreCase.Equals(firstRoot, secondRoot))
        {
            throw new IOException("Atomic publication requires paths on the same volume.");
        }
    }
}

internal sealed class WindowsHandleDurability : IHandleDurability
{
    public void FlushToDisk(ValidatedFileHandle file) =>
        (file ?? throw new ArgumentNullException(nameof(file))).FlushToDisk();
}

internal sealed class WriteThroughPublisher : IWriteThroughPublisher
{
    private readonly IAtomicFileOperations _fileOperations;
    private readonly IHandleDurability _handleDurability;

    public WriteThroughPublisher()
        : this(new WindowsAtomicFileOperations(), new WindowsHandleDurability())
    {
    }

    public WriteThroughPublisher(IAtomicFileOperations fileOperations)
        : this(fileOperations, new WindowsHandleDurability())
    {
    }

    internal WriteThroughPublisher(
        IAtomicFileOperations fileOperations,
        IHandleDurability handleDurability)
    {
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _handleDurability = handleDurability ??
            throw new ArgumentNullException(nameof(handleDurability));
    }

    public ValueTask PublishNewAsync(
        ValidatedFileHandle temporaryFile,
        string finalPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(temporaryFile);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePinnedFile(temporaryFile, expectedHash.Span, flushToDisk: true);

        _fileOperations.RenameNoReplace(temporaryFile, finalPath);
        _handleDurability.FlushToDisk(temporaryFile);
        ValidatePinnedFile(
            temporaryFile,
            expectedHash.Span,
            flushToDisk: true,
            ValidatedFileUse.PostPublication);
        return ValueTask.CompletedTask;
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
        ArgumentNullException.ThrowIfNull(temporaryFile);
        ArgumentNullException.ThrowIfNull(targetFile);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePinnedFile(temporaryFile, expectedHash.Span, flushToDisk: true);
        var targetHash = SHA256.HashData(
            targetFile.ReadAllBytes(flushToDisk: false));
        existingLastKnownGoodFile?.RevalidateIdentity();

        var retainedLastKnownGoodPath = existingLastKnownGoodFile is null
            ? null
            : $"{lastKnownGoodPath}.retained.{Guid.NewGuid():N}";
        var lastKnownGoodRetained = false;
        var targetMoved = false;
        var stagedFileMoved = false;
        try
        {
            if (existingLastKnownGoodFile is not null)
            {
                _fileOperations.RenameNoReplace(
                    existingLastKnownGoodFile,
                    retainedLastKnownGoodPath!);
                lastKnownGoodRetained = true;
                _handleDurability.FlushToDisk(existingLastKnownGoodFile);
            }

            _fileOperations.RenameNoReplace(targetFile, lastKnownGoodPath);
            targetMoved = true;
            _handleDurability.FlushToDisk(targetFile);
            ValidatePinnedFile(targetFile, targetHash, flushToDisk: true);

            _fileOperations.RenameNoReplace(temporaryFile, finalPath);
            stagedFileMoved = true;
            _handleDurability.FlushToDisk(temporaryFile);
            ValidatePinnedFile(
                temporaryFile,
                expectedHash.Span,
                flushToDisk: true,
                ValidatedFileUse.PostPublication);

            if (existingLastKnownGoodFile is not null)
            {
                TryDeleteRetainedLastKnownGood(existingLastKnownGoodFile);
            }

            return ValueTask.CompletedTask;
        }
        catch (Exception primaryFailure)
        {
            if (stagedFileMoved)
            {
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }

            List<Exception>? recoveryFailures = null;
            try
            {
                if (targetMoved)
                {
                    _fileOperations.RenameNoReplace(targetFile, finalPath);
                    _handleDurability.FlushToDisk(targetFile);
                    ValidatePinnedFile(targetFile, targetHash, flushToDisk: true);
                }
            }
            catch (Exception recoveryFailure)
            {
                (recoveryFailures ??= []).Add(recoveryFailure);
            }

            if (lastKnownGoodRetained)
            {
                try
                {
                    _fileOperations.RenameNoReplace(
                        existingLastKnownGoodFile!,
                        lastKnownGoodPath);
                    _handleDurability.FlushToDisk(existingLastKnownGoodFile!);
                    existingLastKnownGoodFile!.RevalidateIdentity();
                }
                catch (Exception recoveryFailure)
                {
                    (recoveryFailures ??= []).Add(recoveryFailure);
                }
            }

            if (recoveryFailures is not null)
            {
                throw new AggregateException(
                    "Projection publication failed and one or more retained files could not be restored without replacement.",
                    new[] { primaryFailure }.Concat(recoveryFailures));
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }
    }

    private void TryDeleteRetainedLastKnownGood(ValidatedFileHandle retainedFile)
    {
        try
        {
            _fileOperations.Delete(retainedFile);
        }
        catch (Exception cleanupFailure) when (!IsFatal(cleanupFailure))
        {
            // The new target has already passed durable readback. Leaving the uniquely named
            // retained copy is safer than reporting the committed publication as failed.
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or AccessViolationException;

    private static void ValidatePinnedFile(
        ValidatedFileHandle file,
        ReadOnlySpan<byte> expectedHash,
        bool flushToDisk,
        ValidatedFileUse? observedUse = null)
    {
        file.RevalidateIdentity();
        var publishedBytes = file.ReadAllBytes(flushToDisk, observedUse);
        var publishedHash = SHA256.HashData(publishedBytes);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, publishedHash))
        {
            throw new InvalidDataException(
                "The pinned publication file differs from its validated bytes.");
        }
    }
}
