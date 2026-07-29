using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Winora.Infrastructure.Persistence;

namespace Winora.Infrastructure.Backups;

internal static partial class SecureBackupDirectoryLayout
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileRenameInfoClass = 3;
    private const int FileDispositionInfoClass = 4;
    private const int ErrorAlreadyExists = 183;

    internal static void CreateDirectoryNew(
        string path,
        byte[]? securityDescriptor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        bool created;
        if (securityDescriptor is null)
        {
            created = CreateDirectory(ToExtendedPath(path), IntPtr.Zero);
        }
        else
        {
            if (securityDescriptor.Length == 0)
            {
                throw new ArgumentException(
                    "A directory security descriptor cannot be empty.",
                    nameof(securityDescriptor));
            }

            var pinned = GCHandle.Alloc(securityDescriptor, GCHandleType.Pinned);
            try
            {
                var attributes = new SecurityAttributes
                {
                    Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                    SecurityDescriptor = pinned.AddrOfPinnedObject(),
                    InheritHandle = 0,
                };
                // Microsoft Learn: https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-createdirectoryw
                // The self-relative descriptor is applied by the kernel as part of create-new.
                created = CreateDirectoryWithSecurity(
                    ToExtendedPath(path),
                    in attributes);
            }
            finally
            {
                pinned.Free();
            }
        }

        if (!created)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorAlreadyExists)
            {
                throw new InvalidDataException(
                    "A backup staging directory must be created with create-new semantics.");
            }

            throw new IOException(
                $"Unable to create the protected backup directory (Win32 error {error}).",
                new Win32Exception(error));
        }
    }

    internal static PinnedDirectory AcquirePinnedDirectory(
        string path,
        bool allowRename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var handle = CreateFile(
            ToExtendedPath(path),
            FileReadAttributes | (allowRename ? DeleteAccess : 0),
            FileShareRead | FileShareWrite | (allowRename ? FileShareDelete : 0),
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(
                $"Unable to pin the protected backup directory (Win32 error {error}).",
                new Win32Exception(error));
        }

        try
        {
            EnsureNotReparsePoint(handle);
            return new PinnedDirectory(handle, ReadIdentity(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static void EnsureSingleLinkRegularFile(string path)
    {
        using var handle = CreateFile(
            ToExtendedPath(path),
            FileReadAttributes,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Unable to inspect a backup artifact (Win32 error {error}).",
                new Win32Exception(error));
        }

        EnsureNotReparsePoint(handle);
        if (!GetFileInformationByHandle(handle, out var info) ||
            (info.FileAttributes & (uint)FileAttributes.Directory) != 0 ||
            info.NumberOfLinks != 1)
        {
            throw new InvalidDataException(
                "Backup artifacts must be regular single-link files.");
        }
    }

    internal static void DeleteTreeWithoutFollowingReparsePoints(
        string path,
        Action<string>? beforeOpenEntry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Directory.Exists(path))
        {
            return;
        }

        DirectoryIdentity expectedIdentity;
        using (var expected = AcquirePinnedDirectory(path, allowRename: true))
        {
            expectedIdentity = expected.Identity;
        }

        DeleteTreeWithoutFollowingReparsePoints(
            path,
            expectedIdentity,
            beforeOpenEntry);
    }

    internal static void DeleteTreeWithoutFollowingReparsePoints(
        string path,
        DirectoryIdentity expectedRootIdentity,
        Action<string>? beforeOpenEntry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var root = OpenForDeletion(path);
        if (root is null)
        {
            throw new IOException(
                "The protected retention root disappeared before handle-bound deletion.");
        }

        if (!root.IsDirectory)
        {
            throw new InvalidDataException(
                "A protected retention root must remain a directory.");
        }

        if (ReadIdentity(root.Handle) != expectedRootIdentity)
        {
            throw new InvalidDataException(
                "The protected retention root identity changed before handle-bound deletion.");
        }

        DeleteChildren(path, root.Handle, beforeOpenEntry);
        MarkDeletePending(root.Handle);
    }

    internal static bool DeleteSingleFileWithoutFollowingReparsePoints(
        string path,
        ValidatedFileIdentity expectedIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var entry = OpenForDeletion(path);
        if (entry is null)
        {
            return false;
        }

        if (entry.IsDirectory)
        {
            throw new InvalidDataException(
                "Refusing to delete a directory through the single-file cleanup path.");
        }

        EnsureSingleLink(entry.Handle);
        var actual = ReadIdentity(entry.Handle);
        if (actual.VolumeSerialNumber != expectedIdentity.VolumeSerialNumber ||
            actual.FileIndex != expectedIdentity.FileIndex)
        {
            throw new InvalidDataException(
                "The cleanup file identity changed before handle-bound deletion.");
        }

        MarkDeletePending(entry.Handle);
        return true;
    }

    private static void DeleteChildren(
        string directoryPath,
        SafeFileHandle directoryHandle,
        Action<string>? beforeOpenEntry)
    {
        _ = directoryHandle;
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(
                     directoryPath,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            beforeOpenEntry?.Invoke(entryPath);
            using var entry = OpenForDeletion(entryPath);
            if (entry is null)
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                DeleteChildren(entryPath, entry.Handle, beforeOpenEntry);
            }
            else
            {
                EnsureSingleLink(entry.Handle);
            }

            MarkDeletePending(entry.Handle);
        }
    }

    private static DeletionHandle? OpenForDeletion(string path)
    {
        var handle = CreateFile(
            ToExtendedPath(path),
            FileReadAttributes | DeleteAccess,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is 2 or 3)
            {
                return null;
            }

            throw new IOException(
                $"Unable to pin an entry for safe deletion (Win32 error {error}).",
                new Win32Exception(error));
        }

        try
        {
            EnsureNotReparsePoint(handle);
            if (!GetFileInformationByHandle(handle, out var info))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException(
                    $"Unable to inspect an entry for safe deletion (Win32 error {error}).",
                    new Win32Exception(error));
            }

            return new DeletionHandle(
                handle,
                (info.FileAttributes & (uint)FileAttributes.Directory) != 0);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void EnsureSingleLink(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info) ||
            info.NumberOfLinks != 1)
        {
            throw new InvalidDataException(
                "Retention refuses files with hard-link aliases.");
        }
    }

    private static void MarkDeletePending(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInfo { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfoClass,
                in disposition,
                (uint)Marshal.SizeOf<FileDispositionInfo>()))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Unable to mark a verified retention entry for deletion (Win32 error {error}).",
                new Win32Exception(error));
        }
    }

    private static void RenameDirectoryNew(
        SafeFileHandle handle,
        string finalPath)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        var fullPath = Path.GetFullPath(finalPath);
        var fileNameBytes = System.Text.Encoding.Unicode.GetBytes(fullPath);
        var fileNameOffset = IntPtr.Size == 8 ? 20 : 12;
        var bufferSize = checked(fileNameOffset + fileNameBytes.Length + sizeof(char));
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            Marshal.WriteByte(buffer, 0, 0);
            Marshal.WriteIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4, IntPtr.Zero);
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 16 : 8, fileNameBytes.Length);
            Marshal.Copy(fileNameBytes, 0, IntPtr.Add(buffer, fileNameOffset), fileNameBytes.Length);

            // Microsoft Learn: https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle
            // Microsoft Learn: https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_rename_info
            if (!SetFileInformationByHandleRaw(
                    handle,
                    FileRenameInfoClass,
                    buffer,
                    (uint)bufferSize))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException(
                    $"Unable to publish the pinned backup directory (Win32 error {error}).",
                    new Win32Exception(error));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static DirectoryIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Unable to identify a backup directory (Win32 error {error}).",
                new Win32Exception(error));
        }

        return new DirectoryIdentity(
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    private static void EnsureNotReparsePoint(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfoClass,
                out var info,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Unable to validate a backup path (Win32 error {error}).",
                new Win32Exception(error));
        }

        if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Backup storage refuses reparse-point directories and files.");
        }
    }

    private static string ToExtendedPath(string path) =>
        path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? path
            : $"\\\\?\\{Path.GetFullPath(path)}";

    internal sealed class PinnedDirectory : IDisposable
    {
        private readonly SafeFileHandle _handle;

        internal PinnedDirectory(
            SafeFileHandle handle,
            DirectoryIdentity identity)
        {
            _handle = handle;
            Identity = identity;
        }

        internal DirectoryIdentity Identity { get; }

        internal void RenameNew(string finalPath) =>
            RenameDirectoryNew(_handle, finalPath);

        internal bool MatchesPath(string path)
        {
            using var candidate = AcquirePinnedDirectory(path, allowRename: true);
            return candidate.Identity == Identity;
        }

        public void Dispose() => _handle.Dispose();
    }

    internal readonly record struct DirectoryIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex);

    private sealed class DeletionHandle : IDisposable
    {
        internal DeletionHandle(SafeFileHandle handle, bool isDirectory)
        {
            Handle = handle;
            IsDirectory = isDirectory;
        }

        internal SafeFileHandle Handle { get; }

        internal bool IsDirectory { get; }

        public void Dispose() => Handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        internal int DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal uint Length;
        internal IntPtr SecurityDescriptor;
        internal int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileAttributeTagInfo
    {
        internal readonly uint FileAttributes;
        internal readonly uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal FileTime CreationTime;
        internal FileTime LastAccessTime;
        internal FileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateDirectoryW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectory(
        string path,
        IntPtr securityAttributes);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateDirectoryW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectoryWithSecurity(
        string path,
        in SecurityAttributes securityAttributes);

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
        out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileAttributeTagInfo information,
        uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeFileHandle file,
        int informationClass,
        in FileDispositionInfo information,
        uint bufferSize);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandleRaw(
        SafeFileHandle file,
        int informationClass,
        IntPtr information,
        uint bufferSize);
}
