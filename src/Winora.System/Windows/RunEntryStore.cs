using Microsoft.Win32;

namespace Winora.System.Windows;

/// <summary>Where a startup entry currently lives.</summary>
public enum RunEntryState
{
    /// <summary>Present in the documented Run key, so Windows launches it at sign-in.</summary>
    Enabled,

    /// <summary>Held in Winora's own key, so Windows does not see it.</summary>
    Disabled,

    /// <summary>Not in either place.</summary>
    Absent,
}

/// <param name="State">Where the entry lives.</param>
/// <param name="Command">The command line, preserved byte-for-byte across a disable.</param>
/// <param name="Kind">The registry value kind, preserved so a re-enable restores the original shape.</param>
/// <param name="IsWritable">Both keys could be opened for writing.</param>
public sealed record RunEntryReading(
    RunEntryState State,
    string? Command,
    RegistryValueKind Kind,
    bool IsWritable);

public enum RunEntryWriteOutcome
{
    Written,
    NotWritten,
    OutcomeUnknown,
}

/// <summary>Moves a documented Run entry between the Run key and Winora's own holding key.</summary>
public interface IRunEntryStore
{
    IReadOnlyList<string> EnabledNames();

    IReadOnlyList<string> DisabledNames();

    RunEntryReading Read(string name);

    RunEntryWriteOutcome Enable(string name);

    RunEntryWriteOutcome Disable(string name);
}

/// <summary>
/// Disables a startup entry by moving its value into a Winora-owned key rather than deleting it.
/// </summary>
/// <remarks>
/// <para>
/// The specification prescribes exactly this shape for Startup <em>folders</em> — move the shortcut
/// to a Winora-managed disabled directory — and the same reasoning applies to Run values. Deleting
/// the value would destroy the entry name, and the name is the only thing that can restore it: an
/// operation is rebuilt from its identifier alone during startup reconciliation, and an identifier
/// cannot carry a 56-byte name within the 96-character catalog limit.
/// </para>
/// <para>
/// The holding key lives in the real user hive, not package storage, so an entry stays recoverable
/// even if Winora is uninstalled — by hand if necessary.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
/// </remarks>
public sealed class WindowsRunEntryStore : IRunEntryStore
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string DisabledKeyPath = @"Software\Winora\DisabledStartup";

    public IReadOnlyList<string> EnabledNames() => Names(RunKeyPath);

    public IReadOnlyList<string> DisabledNames() => Names(DisabledKeyPath);

    public RunEntryReading Read(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var writable = IsWritable();

        if (TryReadValue(RunKeyPath, name, out var command, out var kind))
        {
            return new RunEntryReading(RunEntryState.Enabled, command, kind, writable);
        }

        if (TryReadValue(DisabledKeyPath, name, out command, out kind))
        {
            return new RunEntryReading(RunEntryState.Disabled, command, kind, writable);
        }

        return new RunEntryReading(RunEntryState.Absent, null, RegistryValueKind.String, writable);
    }

    public RunEntryWriteOutcome Enable(string name) => Move(name, DisabledKeyPath, RunKeyPath);

    public RunEntryWriteOutcome Disable(string name) => Move(name, RunKeyPath, DisabledKeyPath);

    /// <summary>
    /// Writes the destination before removing the source. A crash between the two leaves the entry
    /// in both places, which the next read resolves as enabled and a repeated move corrects; the
    /// opposite order could lose the command entirely.
    /// </summary>
    private static RunEntryWriteOutcome Move(string name, string fromPath, string toPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (!TryReadValue(fromPath, name, out var command, out var kind) || command is null)
        {
            return RunEntryWriteOutcome.NotWritten;
        }

        try
        {
            using (var destination = Registry.CurrentUser.CreateSubKey(toPath, writable: true))
            {
                if (destination is null)
                {
                    return RunEntryWriteOutcome.NotWritten;
                }

                destination.SetValue(name, command, kind);
            }

            using var source = Registry.CurrentUser.OpenSubKey(fromPath, writable: true);
            if (source is null)
            {
                // The value now exists in both places and the next read reports it as enabled.
                return RunEntryWriteOutcome.OutcomeUnknown;
            }

            source.DeleteValue(name, throwOnMissingValue: false);
            return RunEntryWriteOutcome.Written;
        }
        catch (UnauthorizedAccessException)
        {
            return RunEntryWriteOutcome.NotWritten;
        }
        catch (Exception)
        {
            return RunEntryWriteOutcome.OutcomeUnknown;
        }
    }

    private static bool TryReadValue(
        string path,
        string name,
        out string? command,
        out RegistryValueKind kind)
    {
        command = null;
        kind = RegistryValueKind.String;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            var raw = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is not string text)
            {
                return false;
            }

            var observed = key!.GetValueKind(name);
            if (observed is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            {
                return false;
            }

            command = text;
            kind = observed;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> Names(string path)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            return key is null
                ? []
                : key.GetValueNames().Where(static name => !string.IsNullOrEmpty(name)).ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool IsWritable()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            return run is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
