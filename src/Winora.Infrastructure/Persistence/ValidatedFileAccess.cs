using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Winora.Infrastructure.Persistence;

internal readonly record struct ValidatedFileIdentity(
    uint VolumeSerialNumber,
    ulong FileIndex);

internal enum ValidatedFileUse
{
    PublicRead = 0,
    ProjectionProbe = 1,
    StagingReadback = 2,
    PrePublication = 3,
    PostPublication = 4,
}

internal interface IValidatedFileObserver
{
    void OnValidated(
        string path,
        ValidatedFileIdentity identity,
        ValidatedFileUse use);

    byte[] TransformRead(
        string path,
        ValidatedFileIdentity identity,
        ValidatedFileUse use,
        byte[] bytes) => bytes;
}

internal interface IValidatedFileAccess
{
    ValidatedFileHandle Open(
        string path,
        FileAccess access,
        ValidatedFileUse use);

    ValidatedFileHandle? TryOpen(
        string path,
        FileAccess access,
        ValidatedFileUse use);

    ValidatedFileHandle OpenPinnedRead(
        string path,
        ValidatedFileUse use);

    ValidatedFileHandle OpenForMutation(
        string path,
        ValidatedFileUse use);

    ValidatedFileHandle? TryOpenForMutation(
        string path,
        ValidatedFileUse use);
}

internal sealed partial class WindowsValidatedFileAccess(
    IValidatedFileObserver? observer = null) : IValidatedFileAccess
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint Delete = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagWriteThrough = 0x80000000;

    public ValidatedFileHandle Open(
        string path,
        FileAccess access,
        ValidatedFileUse use) => OpenCore(
            path,
            access,
            use,
            canMutateEntry: false,
            blocksEntryMutation: false);

    public ValidatedFileHandle OpenPinnedRead(
        string path,
        ValidatedFileUse use) => OpenCore(
            path,
            FileAccess.Read,
            use,
            canMutateEntry: false,
            blocksEntryMutation: true);

    public ValidatedFileHandle OpenForMutation(
        string path,
        ValidatedFileUse use) =>
        OpenCore(
            path,
            FileAccess.ReadWrite,
            use,
            canMutateEntry: true,
            blocksEntryMutation: true);

    private ValidatedFileHandle OpenCore(
        string path,
        FileAccess access,
        ValidatedFileUse use,
        bool canMutateEntry,
        bool blocksEntryMutation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (access is not FileAccess.Read and not FileAccess.ReadWrite)
        {
            throw new ArgumentOutOfRangeException(nameof(access));
        }

        var extendedPath = path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? path
            : $"\\\\?\\{path}";
        var desiredAccess = GenericRead |
            (access == FileAccess.ReadWrite ? GenericWrite : 0) |
            (canMutateEntry ? Delete : 0);
        var shareMode = blocksEntryMutation
            ? FileShareRead
            : FileShareRead | FileShareWrite | FileShareDelete;

        // Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew
        var handle = CreateFile(
            extendedPath,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint |
            FileFlagSequentialScan |
            (canMutateEntry ? FileFlagWriteThrough : 0),
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is 2 or 3)
            {
                throw new FileNotFoundException("The validated Winora file does not exist.", path);
            }

            throw new IOException(
                $"Unable to open a validated Winora file (Win32 error {error}).",
                new Win32Exception(error));
        }

        try
        {
            var identity = ValidateOpenHandle(handle);
            observer?.OnValidated(path, identity, use);
            return new ValidatedFileHandle(
                handle,
                path,
                identity,
                access,
                use,
                observer,
                canMutateEntry);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public ValidatedFileHandle? TryOpen(
        string path,
        FileAccess access,
        ValidatedFileUse use)
    {
        try
        {
            return Open(path, access, use);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public ValidatedFileHandle? TryOpenForMutation(
        string path,
        ValidatedFileUse use)
    {
        try
        {
            return OpenForMutation(path, use);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    internal static ValidatedFileIdentity ValidateOpenHandle(
        SafeFileHandle handle,
        bool requireSingleLink = true)
    {
        ArgumentNullException.ThrowIfNull(handle);

        // Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle
        if (!GetFileInformationByHandle(handle, out var information))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Unable to identify a Winora file (Win32 error {error}).",
                new Win32Exception(error));
        }

        if ((information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            (information.FileAttributes & (uint)FileAttributes.Directory) != 0)
        {
            throw new IOException("Winora persistence accepts only ordinary non-reparse files.");
        }

        if (requireSingleLink && information.NumberOfLinks != 1)
        {
            throw new IOException("Winora persistence rejects files with hard-link aliases.");
        }

        return new ValidatedFileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        internal readonly uint LowDateTime;
        internal readonly uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ByHandleFileInformation
    {
        internal readonly uint FileAttributes;
        internal readonly NativeFileTime CreationTime;
        internal readonly NativeFileTime LastAccessTime;
        internal readonly NativeFileTime LastWriteTime;
        internal readonly uint VolumeSerialNumber;
        internal readonly uint FileSizeHigh;
        internal readonly uint FileSizeLow;
        internal readonly uint NumberOfLinks;
        internal readonly uint FileIndexHigh;
        internal readonly uint FileIndexLow;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);
}

internal sealed partial class ValidatedFileHandle : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly FileAccess _access;
    private readonly ValidatedFileUse _use;
    private readonly IValidatedFileObserver? _observer;
    private readonly bool _canMutateEntry;

    internal ValidatedFileHandle(
        SafeFileHandle handle,
        string path,
        ValidatedFileIdentity identity,
        FileAccess access,
        ValidatedFileUse use,
        IValidatedFileObserver? observer,
        bool canMutateEntry)
    {
        _handle = handle;
        Path = path;
        CurrentPath = path;
        Identity = identity;
        _access = access;
        _use = use;
        _observer = observer;
        _canMutateEntry = canMutateEntry;
    }

    internal string Path { get; }

    internal string CurrentPath { get; private set; }

    internal ValidatedFileIdentity Identity { get; }

    internal bool HasBeenRenamed { get; private set; }

    internal void RevalidateIdentity(bool requireSingleLink = true)
    {
        var actualIdentity = WindowsValidatedFileAccess.ValidateOpenHandle(
            _handle,
            requireSingleLink);
        if (actualIdentity != Identity)
        {
            throw new IOException("The validated Winora file identity changed while it was pinned.");
        }
    }

    internal byte[] ReadAllBytes(
        bool flushToDisk,
        ValidatedFileUse? observedUse = null)
    {
        var length = RandomAccess.GetLength(_handle);
        if (length < 0 || length > int.MaxValue)
        {
            throw new InvalidDataException("The persisted Winora document is too large.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>((int)length);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = RandomAccess.Read(_handle, bytes.AsSpan(offset), offset);
            if (read == 0)
            {
                throw new EndOfStreamException("The validated Winora file changed while it was read.");
            }

            offset += read;
        }

        if (flushToDisk)
        {
            if (_access != FileAccess.ReadWrite)
            {
                throw new InvalidOperationException("A durable flush requires a read/write validated handle.");
            }

            // Microsoft Learn: https://learn.microsoft.com/en-us/dotnet/api/system.io.randomaccess.flushtodisk?view=net-10.0
            RandomAccess.FlushToDisk(_handle);
        }

        RevalidateIdentity();

        return _observer?.TransformRead(Path, Identity, observedUse ?? _use, bytes) ?? bytes;
    }

    internal void RenameNoReplace(string destinationPath)
    {
        EnsureEntryMutationAllowed(requireSingleLink: true);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullPath = System.IO.Path.GetFullPath(destinationPath);
        var fileName = fullPath.ToCharArray();
        var fileNameBytes = checked((uint)(fileName.Length * sizeof(char)));
        var fileNameOffset = IntPtr.Size == 8 ? 20 : 12;
        var bufferSize = checked(fileNameOffset + (int)fileNameBytes + sizeof(char));
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            Marshal.WriteInt32(buffer, 0, 0);
            Marshal.WriteIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4, IntPtr.Zero);
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 16 : 8, checked((int)fileNameBytes));
            Marshal.Copy(fileName, 0, IntPtr.Add(buffer, fileNameOffset), fileName.Length);

            // Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle
            // Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info
            if (!SetFileInformationByHandle(
                    _handle,
                    FileRenameInfo,
                    buffer,
                    checked((uint)bufferSize)))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException(
                    $"The handle-bound Winora rename failed with Win32 error {error}.",
                    new Win32Exception(error));
            }

            HasBeenRenamed = true;
            CurrentPath = fullPath;

        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal void MarkDelete()
    {
        EnsureEntryMutationAllowed(requireSingleLink: false);
        var deleteFile = new byte[] { 1 };

        // Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle
        if (!SetFileInformationByHandle(
                _handle,
                FileDispositionInfo,
                deleteFile,
                checked((uint)deleteFile.Length)))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"The handle-bound Winora delete failed with Win32 error {error}.",
                new Win32Exception(error));
        }
    }

    internal void FlushToDisk()
    {
        if (_access != FileAccess.ReadWrite)
        {
            throw new InvalidOperationException(
                "A durable flush requires a read/write validated handle.");
        }

        RandomAccess.FlushToDisk(_handle);
        RevalidateIdentity();
    }

    private void EnsureEntryMutationAllowed(bool requireSingleLink)
    {
        if (!_canMutateEntry)
        {
            throw new InvalidOperationException(
                "The validated Winora handle was not opened for entry mutation.");
        }

        RevalidateIdentity(requireSingleLink);
    }

    public void Dispose() => _handle.Dispose();

    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        byte[] fileInformation,
        uint bufferSize);
}
