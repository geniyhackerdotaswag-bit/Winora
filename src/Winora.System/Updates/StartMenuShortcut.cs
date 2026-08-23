using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Winora.System.Updates;

/// <summary>Writes a Windows shortcut file.</summary>
public interface IShortcutWriter
{
    /// <summary>Writes <paramref name="shortcutPath" /> pointing at <paramref name="targetPath" />.</summary>
    /// <returns>True when the shortcut was written.</returns>
    bool Write(string shortcutPath, string targetPath, string description);
}

/// <summary>
/// Writes a <c>.lnk</c> through the shell's own interface.
/// </summary>
/// <remarks>
/// <para>
/// A shortcut is a structured binary file and there is no supported way to produce one except by
/// asking the shell. This is the standard interop for it, unchanged since it was documented.
/// </para>
/// <para>
/// Behind an interface because everything above it must be testable without leaving files in a real
/// Start menu, and because a failure here has to be survivable: a program in the right place with no
/// menu entry still works, and the installer treats a refusal as a shrug rather than an error.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ishelllinkw
/// </remarks>
public sealed class StartMenuShortcut : IShortcutWriter
{
    public bool Write(string shortcutPath, string targetPath, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        object? instance = null;

        try
        {
            var directory = Path.GetDirectoryName(shortcutPath);
            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
            }

            // Activated by CLSID rather than through a declared coclass. A [ComImport] class cannot
            // be sealed, and an unsealed private type trips CA1852 — which, with warnings as errors,
            // means the canonical declaration will not build here. This asks the runtime for the
            // same object without declaring a type at all.
            var type = Type.GetTypeFromCLSID(ShellLinkClsid);
            instance = type is null ? null : Activator.CreateInstance(type);

            if (instance is not IShellLinkW link)
            {
                return false;
            }

            link.SetPath(targetPath);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetPath) ?? string.Empty);
            link.SetDescription(description ?? string.Empty);

            ((IPersistFile)instance).Save(shortcutPath, fRemember: true);
            return true;
        }
        catch (Exception)
        {
            // COM unavailable, the folder not writable, or the shell refusing. The caller carries on
            // without a menu entry rather than failing an installation over one.
            return false;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.ReleaseComObject(instance);
            }
        }
    }

    /// <summary>The shell's link object.</summary>
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maxPath,
            nint find,
            int flags);

        void GetIDList(out nint list);

        void SetIDList(nint list);

        void GetDescription(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name,
            int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
            int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        void GetArguments(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,
            int maxArguments);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int show);

        void SetShowCmd(int show);

        void GetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
            int iconPathLength,
            out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, int reserved);

        void Resolve(nint window, int flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
