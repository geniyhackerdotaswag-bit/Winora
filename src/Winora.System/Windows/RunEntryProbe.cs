using Microsoft.Win32;

namespace Winora.System.Windows;

/// <summary>Which documented Run key an entry came from.</summary>
public enum RunEntryScope
{
    /// <summary>HKCU: owned by the signed-in user, changeable without elevation.</summary>
    CurrentUser,

    /// <summary>HKLM: machine-wide, requires administrator rights to change.</summary>
    LocalMachine,
}

/// <param name="Name">The registry value name, exactly as stored.</param>
/// <param name="Command">The command line the entry launches.</param>
/// <param name="Scope">Which Run key it came from.</param>
/// <param name="IsDocumentedKind">
/// The value is a string kind, as the Run key documentation describes. A value of another kind is
/// reported rather than coerced, because Winora would otherwise misrepresent what is there.
/// </param>
public sealed record RunEntry(
    string Name,
    string Command,
    RunEntryScope Scope,
    bool IsDocumentedKind);

/// <summary>Reads the documented Run keys. Never writes.</summary>
public interface IRunEntryProbe
{
    IReadOnlyList<RunEntry> Read();
}

/// <summary>
/// Enumerates the documented Run and RunOnce integration points. Read-only: this type exists to
/// tell the user what launches at sign-in and from where, and has no path that changes anything.
/// </summary>
/// <remarks>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
/// </remarks>
public sealed class WindowsRunEntryProbe : IRunEntryProbe
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public IReadOnlyList<RunEntry> Read()
    {
        var entries = new List<RunEntry>();
        Collect(Registry.CurrentUser, RunEntryScope.CurrentUser, entries);
        Collect(Registry.LocalMachine, RunEntryScope.LocalMachine, entries);
        return entries;
    }

    private static void Collect(RegistryKey hive, RunEntryScope scope, List<RunEntry> entries)
    {
        RegistryKey? key = null;
        try
        {
            key = hive.OpenSubKey(RunKeyPath, writable: false);
            if (key is null)
            {
                return;
            }

            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var kind = key.GetValueKind(name);
                var isDocumented = kind is RegistryValueKind.String or RegistryValueKind.ExpandString;
                var raw = key.GetValue(name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames);

                entries.Add(new RunEntry(
                    name,
                    isDocumented ? raw as string ?? string.Empty : string.Empty,
                    scope,
                    isDocumented));
            }
        }
        catch (Exception)
        {
            // An unreadable hive is reported as no entries from that scope rather than taking the
            // whole inventory down; the screen still shows everything Winora could read.
        }
        finally
        {
            key?.Dispose();
        }
    }
}
