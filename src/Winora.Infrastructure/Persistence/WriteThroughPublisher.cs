using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Winora.Infrastructure.Persistence;

public interface IWriteThroughPublisher
{
    ValueTask PublishNewAsync(
        string temporaryPath,
        string finalPath,
        CancellationToken cancellationToken);

    ValueTask ReplaceProjectionAsync(
        string temporaryPath,
        string finalPath,
        string lastKnownGoodPath,
        CancellationToken cancellationToken);
}

public interface IAtomicFileOperations
{
    void MoveNewFileWriteThrough(string temporaryPath, string finalPath);

    void ReplaceFile(string temporaryPath, string finalPath, string lastKnownGoodPath);
}

public interface IFileDurability
{
    void FlushToDisk(FileStream stream);

    byte[] ReopenReadAndFlush(string path);
}

public sealed class WindowsFileDurability : IFileDurability
{
    public void FlushToDisk(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Microsoft Learn: https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-10.0
        stream.Flush(flushToDisk: true);
    }

    public byte[] ReopenReadAndFlush(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        // Microsoft Learn: https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-10.0
        stream.Flush(flushToDisk: true);
        return buffer.ToArray();
    }
}

public sealed partial class WindowsAtomicFileOperations : IAtomicFileOperations
{
    private const uint MoveFileWriteThrough = 0x00000008;

    public void MoveNewFileWriteThrough(string temporaryPath, string finalPath)
    {
        ValidateSameVolume(temporaryPath, finalPath);

        // Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw
        if (!MoveFileEx(temporaryPath, finalPath, MoveFileWriteThrough))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"The write-through publication move failed with Win32 error {error}.",
                new Win32Exception(error));
        }
    }

    public void ReplaceFile(string temporaryPath, string finalPath, string lastKnownGoodPath)
    {
        ValidateSameVolume(temporaryPath, finalPath);
        ValidateSameVolume(finalPath, lastKnownGoodPath);

        // Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew
        File.Replace(temporaryPath, finalPath, lastKnownGoodPath, ignoreMetadataErrors: false);
    }

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

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);
}

public sealed class WriteThroughPublisher : IWriteThroughPublisher
{
    private readonly IAtomicFileOperations _fileOperations;
    private readonly IFileDurability _fileDurability;

    public WriteThroughPublisher()
        : this(new WindowsAtomicFileOperations(), new WindowsFileDurability())
    {
    }

    public WriteThroughPublisher(
        IAtomicFileOperations fileOperations,
        IFileDurability fileDurability)
    {
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _fileDurability = fileDurability ?? throw new ArgumentNullException(nameof(fileDurability));
    }

    public ValueTask PublishNewAsync(
        string temporaryPath,
        string finalPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expectedHash = HashFile(temporaryPath);

        _fileOperations.MoveNewFileWriteThrough(temporaryPath, finalPath);
        VerifyPublishedFile(finalPath, expectedHash);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask ReplaceProjectionAsync(
        string temporaryPath,
        string finalPath,
        string lastKnownGoodPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expectedHash = HashFile(temporaryPath);

        _fileOperations.ReplaceFile(temporaryPath, finalPath, lastKnownGoodPath);
        VerifyPublishedFile(finalPath, expectedHash);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private static byte[] HashFile(string path) => SHA256.HashData(File.ReadAllBytes(path));

    private void VerifyPublishedFile(string path, ReadOnlySpan<byte> expectedHash)
    {
        var publishedBytes = _fileDurability.ReopenReadAndFlush(path);
        var publishedHash = SHA256.HashData(publishedBytes);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, publishedHash))
        {
            throw new InvalidDataException(
                "The published file differs from the durably flushed staging file.");
        }
    }
}
