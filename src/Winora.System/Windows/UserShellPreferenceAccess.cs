using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace Winora.System.Windows;

/// <param name="IsKeyAccessible">The Explorer\Advanced key could be opened for reading.</param>
/// <param name="IsValuePresent">The value exists. When false, Windows applies its own default.</param>
/// <param name="Value">The observed number, or null when the value is absent or of the wrong kind.</param>
/// <param name="IsKindAsDocumented">The live value kind matches what Microsoft documents.</param>
/// <param name="IsKeyWritable">
/// The key opened for writing. Probed rather than assumed from readability, so a policy-locked or
/// otherwise protected key degrades before a plan is offered instead of failing at the write.
/// </param>
public sealed record ShellPreferenceReading(
    bool IsKeyAccessible,
    bool IsValuePresent,
    int? Value,
    bool IsKindAsDocumented,
    bool IsKeyWritable = true)
{
    /// <summary>
    /// Whether Winora may plan a change from this reading. An absent value is usable, because
    /// absence is a state Winora can restore exactly by deleting the value again. A present value of
    /// an undocumented kind is not usable: writing a DWORD over something else would change the
    /// shape of the user's registry while claiming to have changed a setting.
    /// </summary>
    public bool IsUsable => IsKeyAccessible && IsKindAsDocumented;
}

/// <summary>The outcome of one write. Mirrors the visual-effect adapter's honesty about unknowns.</summary>
public enum ShellPreferenceWriteOutcome
{
    Written,
    NotWritten,
    OutcomeUnknown,
}

/// <summary>
/// Narrow adapter over the documented per-user Explorer values. Injected so operation behavior is
/// provable without changing the developer's own Windows session. Displays no UI.
/// </summary>
public interface IUserShellPreferenceAccess
{
    ShellPreferenceReading Read(DocumentedShellValue entry);

    ShellPreferenceWriteOutcome Write(DocumentedShellValue entry, int value);

    /// <summary>Restores absence, which is a different state from writing a default.</summary>
    ShellPreferenceWriteOutcome Delete(DocumentedShellValue entry);
}

/// <summary>
/// Documented <c>HKCU\...\Explorer\Advanced</c> implementation with the documented shell change
/// notification. Winora never terminates <c>explorer.exe</c>: restarting the shell is not a
/// documented mechanism, so a pending change is reported through the plan's restart requirement.
/// </summary>
/// <remarks>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/apps/develop/settings/settings-windows-11
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shchangenotify
/// </remarks>
public sealed partial class WindowsUserShellPreferenceAccess : IUserShellPreferenceAccess
{
    private const string AdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>SHCNE_ASSOCCHANGED: a general shell notification that settings changed.</summary>
    private const uint ShcneAssocChanged = 0x08000000;

    /// <summary>SHCNF_IDLIST.</summary>
    private const uint ShcnfIdList = 0x0000;

    public ShellPreferenceReading Read(DocumentedShellValue entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var key = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath, writable: false);
        if (key is null)
        {
            return new ShellPreferenceReading(false, false, null, true, false);
        }

        var writable = IsWritable();

        var raw = key.GetValue(entry.ValueName, null);
        if (raw is null)
        {
            // Absent is a real, restorable state, not a zero.
            return new ShellPreferenceReading(true, false, null, true, writable);
        }

        var kind = key.GetValueKind(entry.ValueName);
        if (kind != entry.DocumentedKind || raw is not int value)
        {
            return new ShellPreferenceReading(true, true, null, false, writable);
        }

        return new ShellPreferenceReading(true, true, value, true, writable);
    }

    /// <summary>
    /// Opens the key for writing and closes it again without touching a value. Asking the question
    /// directly is the only honest answer; readability does not imply write access.
    /// </summary>
    private static bool IsWritable()
    {
        try
        {
            using var writableKey = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath, writable: true);
            return writableKey is not null;
        }
        catch (SecurityException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public ShellPreferenceWriteOutcome Write(DocumentedShellValue entry, int value)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.AllowedValues.Contains(value))
        {
            // A value outside the documented set is refused rather than written and hoped for.
            return ShellPreferenceWriteOutcome.NotWritten;
        }

        return Mutate(entry, key => key.SetValue(entry.ValueName, value, entry.DocumentedKind));
    }

    public ShellPreferenceWriteOutcome Delete(DocumentedShellValue entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Mutate(entry, key => key.DeleteValue(entry.ValueName, throwOnMissingValue: false));
    }

    private static ShellPreferenceWriteOutcome Mutate(DocumentedShellValue entry, Action<RegistryKey> mutate)
    {
        RegistryKey? key = null;
        try
        {
            key = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath, writable: true);
            if (key is null)
            {
                return ShellPreferenceWriteOutcome.NotWritten;
            }

            mutate(key);
        }
        catch (UnauthorizedAccessException)
        {
            return ShellPreferenceWriteOutcome.NotWritten;
        }
        catch (Exception)
        {
            // The write may or may not have landed. Reporting "nothing happened" here would let the
            // coordinator treat an uncertain state as clean, so it is escalated instead.
            return ShellPreferenceWriteOutcome.OutcomeUnknown;
        }
        finally
        {
            key?.Dispose();
        }

        SHChangeNotify(ShcneAssocChanged, ShcnfIdList, nint.Zero, nint.Zero);
        return ShellPreferenceWriteOutcome.Written;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    private static partial void SHChangeNotify(uint wEventId, uint uFlags, nint dwItem1, nint dwItem2);
}
