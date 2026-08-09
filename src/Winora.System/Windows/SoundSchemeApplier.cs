using Microsoft.Win32;

namespace Winora.System.Windows;

/// <param name="Applied">Events whose sound was set.</param>
/// <param name="Skipped">Events that could not be set.</param>
public readonly record struct SoundApplyResult(int Applied, int Skipped);

/// <summary>Points the system sound events at a set of files, and puts them back.</summary>
public interface ISoundSchemeApplier
{
    SoundApplyResult Apply(IReadOnlyDictionary<SoundEvent, string> files);

    /// <summary>Restores every event Winora can change to the sound Windows recorded for it.</summary>
    SoundApplyResult RestoreDefaults();

    /// <summary>Silences every event Winora can change.</summary>
    SoundApplyResult Silence();
}

/// <summary>
/// Writes the per-event sound paths under <c>HKCU\AppEvents</c>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike cursors, sounds have no documented API that bypasses the registry: a sound scheme is
/// registry state by construction. That puts this domain behind the same signing work as the
/// taskbar and startup ones — an unsigned package's registry writes are redirected into its own
/// container, and the app cannot tell the difference from inside. Verify from an unpackaged process
/// before believing this worked.
/// </para>
/// <para>
/// One thing is different and in this domain's favour: these are edits to values that already
/// exist, and a measured write of that shape did reach the real hive, unlike the creation of a new
/// key. That is a reason to test it, not a reason to assume it.
/// </para>
/// <para>
/// Undo never relies on Winora remembering anything. Windows keeps each event's stock sound in a
/// <c>.Default</c> subkey beside the live <c>.Current</c> one, so restoring is copying what the
/// system already recorded — which stays correct even if Winora's own state is lost.
/// </para>
/// </remarks>
public sealed class SoundSchemeApplier : ISoundSchemeApplier
{
    private const string AppsKey = @"AppEvents\Schemes\Apps\.Default";

    /// <summary>
    /// Winora's events mapped to the names Windows uses. Only these are touched; the remaining
    /// fifty-odd events are silent by default and filling them would make the desktop noisier.
    /// </summary>
    private static readonly IReadOnlyDictionary<SoundEvent, string> EventKeys =
        new Dictionary<SoundEvent, string>
        {
            [SoundEvent.Notification] = ".Default",
            [SoundEvent.DeviceConnect] = "DeviceConnect",
            [SoundEvent.DeviceDisconnect] = "DeviceDisconnect",
            [SoundEvent.DeviceFail] = "DeviceFail",
            [SoundEvent.LowBattery] = "LowBatteryAlarm",
            [SoundEvent.CriticalBattery] = "CriticalBatteryAlarm",
            [SoundEvent.Mail] = "MailBeep",
        };

    public SoundApplyResult Apply(IReadOnlyDictionary<SoundEvent, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var applied = 0;
        var skipped = 0;

        foreach (var (soundEvent, keyName) in EventKeys)
        {
            if (!files.TryGetValue(soundEvent, out var file) || !File.Exists(file))
            {
                skipped++;
                continue;
            }

            if (TryWriteCurrent(keyName, file))
            {
                applied++;
            }
            else
            {
                skipped++;
            }
        }

        return new SoundApplyResult(applied, skipped);
    }

    public SoundApplyResult RestoreDefaults()
    {
        var applied = 0;
        var skipped = 0;

        foreach (var keyName in EventKeys.Values)
        {
            var stock = ReadValue($@"{AppsKey}\{keyName}\.Default");
            if (stock is null || !TryWriteCurrent(keyName, stock))
            {
                skipped++;
                continue;
            }

            applied++;
        }

        return new SoundApplyResult(applied, skipped);
    }

    public SoundApplyResult Silence()
    {
        var applied = 0;
        var skipped = 0;

        foreach (var keyName in EventKeys.Values)
        {
            // An empty path is how Windows itself records "this event makes no sound", which is
            // what the shipped .None scheme contains.
            if (TryWriteCurrent(keyName, string.Empty))
            {
                applied++;
            }
            else
            {
                skipped++;
            }
        }

        return new SoundApplyResult(applied, skipped);
    }

    private static bool TryWriteCurrent(string eventKeyName, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{AppsKey}\{eventKeyName}\.Current", writable: true);
            if (key is null)
            {
                return false;
            }

            // The default value of the .Current subkey is the sound path; Windows reads it on every
            // play, so nothing has to be told to reload.
            key.SetValue(string.Empty, value, RegistryValueKind.String);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? ReadValue(string path)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            return key?.GetValue(string.Empty) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
