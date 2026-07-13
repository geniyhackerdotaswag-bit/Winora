using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Winora.Infrastructure.Paths;

internal sealed partial class SecureOwnedPathLease : IDisposable
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileAttributeTagInfoClass = 9;

    private readonly List<SafeFileHandle> _directoryHandles;

    private SecureOwnedPathLease(List<SafeFileHandle> directoryHandles) =>
        _directoryHandles = directoryHandles;

    internal static SecureOwnedPathLease Acquire(
        WinoraDataPaths paths,
        string ownedFilePath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var fullPath = paths.EnsureOwnedFilePath(ownedFilePath);
        var parent = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException("The owned file must have a parent directory.", nameof(ownedFilePath));
        var volumeRoot = Path.GetPathRoot(parent);
        if (string.IsNullOrWhiteSpace(volumeRoot) || volumeRoot.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new IOException("Winora persistence requires a fixed local-volume root.");
        }

        var handles = new List<SafeFileHandle>();
        try
        {
            var current = Path.TrimEndingDirectorySeparator(volumeRoot) + Path.DirectorySeparatorChar;
            PinDirectory(current, denyWrite: false, handles);

            var relative = Path.GetRelativePath(current, parent);
            var ownedRootReached = false;
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                // Microsoft Learn: https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.createdirectory?view=net-10.0
                Directory.CreateDirectory(current);
                ownedRootReached |= StringComparer.OrdinalIgnoreCase.Equals(
                    Path.TrimEndingDirectorySeparator(current),
                    paths.RootDirectory);
                PinDirectory(current, ownedRootReached, handles);
            }

            return new SecureOwnedPathLease(handles);
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    public void Dispose() => DisposeHandles(_directoryHandles);

    internal static void RejectReparseLeafIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extendedPath = path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? path
            : $"\\\\?\\{path}";
        using var handle = CreateFile(
            extendedPath,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 2 or 3)
            {
                return;
            }

            throw new IOException(
                $"Unable to inspect a Winora persistence leaf (Win32 error {error}).",
                new Win32Exception(error));
        }

        EnsureNotReparsePoint(handle);
    }

    private static void PinDirectory(
        string path,
        bool denyWrite,
        List<SafeFileHandle> handles)
    {
        var extendedPath = path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? path
            : $"\\\\?\\{path}";
        var handle = CreateFile(
            extendedPath,
            FileReadAttributes,
            denyWrite ? FileShareRead : FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(
                $"Unable to pin the Winora persistence directory (Win32 error {error}).",
                new Win32Exception(error));
        }

        try
        {
            EnsureNotReparsePoint(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        handles.Add(handle);
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
                $"Unable to validate the Winora persistence path (Win32 error {error}).",
                new Win32Exception(error));
        }

        if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Winora persistence refuses reparse points in its fixed root.");
        }
    }

    private static void DisposeHandles(List<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }

        handles.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileAttributeTagInfo
    {
        internal readonly uint FileAttributes;
        internal readonly uint ReparseTag;
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
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);
}
