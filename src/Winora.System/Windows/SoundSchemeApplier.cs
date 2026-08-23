using Microsoft.Win32;

namespace Winora.System.Windows;

/// <param name="Applied">Events that were given one of Winora's sounds.</param>
/// <param name="Silenced">
/// Events Winora has no sound for and muted, so nothing stock plays alongside its own.
/// </param>
/// <param name="Skipped">Events that could not be written at all.</param>
public readonly record struct SoundApplyResult(int Applied, int Silenced, int Skipped);

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
    private const string AppsRoot = @"AppEvents\Schemes\Apps";

    /// <summary>The shell's own events. Winora's sounds map onto names in here.</summary>
    private const string DefaultApp = ".Default";

    private const string AppsKey = $@"{AppsRoot}\{DefaultApp}";

    /// <summary>
    /// Winora's events mapped to the names Windows uses.
    /// </summary>
    /// <remarks>
    /// These are the events Winora has a sound for. Everything else Windows can play is silenced
    /// rather than left alone — see <see cref="Apply" /> for why.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, SoundEvent> EventKeys =
        new Dictionary<string, SoundEvent>(StringComparer.OrdinalIgnoreCase)
        {
            [".Default"] = SoundEvent.Notification,
            ["Notification.Default"] = SoundEvent.Notification,

            ["DeviceConnect"] = SoundEvent.DeviceConnect,
            ["ProximityConnection"] = SoundEvent.DeviceConnect,
            ["DeviceDisconnect"] = SoundEvent.DeviceDisconnect,
            ["DeviceFail"] = SoundEvent.DeviceFail,

            ["LowBatteryAlarm"] = SoundEvent.LowBattery,
            ["CriticalBatteryAlarm"] = SoundEvent.CriticalBattery,

            ["MailBeep"] = SoundEvent.Mail,
            ["Notification.Mail"] = SoundEvent.Mail,
            ["FaxBeep"] = SoundEvent.Mail,

            ["SystemAsterisk"] = SoundEvent.Information,
            ["SystemNotification"] = SoundEvent.Information,
            ["Notification.Proximity"] = SoundEvent.Information,

            ["SystemExclamation"] = SoundEvent.Warning,

            ["SystemHand"] = SoundEvent.Error,

            ["Notification.IM"] = SoundEvent.Message,
            ["Notification.SMS"] = SoundEvent.Message,
            ["MessageNudge"] = SoundEvent.Message,

            ["Notification.Reminder"] = SoundEvent.Reminder,

            ["WindowsUAC"] = SoundEvent.Elevation,

            ["WindowsLogon"] = SoundEvent.Logon,
            ["WindowsUnlock"] = SoundEvent.Logon,
        };

    /// <summary>
    /// События, которые не глушатся никогда, даже если у Winora нет для них звука.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ошибка, найденная при замере 2026-08-09: «заглушить всё остальное» задело
    /// <c>Notification.Looping.Alarm*</c> и <c>Notification.Looping.Call*</c> — это будильники
    /// приложения «Часы» и рингтоны входящих звонков. Человек, поставивший будильник, не проснулся
    /// бы, и связать это с выбором звуковой схемы было бы нельзя.
    /// </para>
    /// <para>
    /// Приложение <c>sapisvr</c> — звуковые подтверждения распознавания речи. Это не оформление, а
    /// средство доступности: они сообщают, слышит ли система голос. Тишина вместо них лишает
    /// человека единственной обратной связи.
    /// </para>
    /// <para>
    /// Эти события остаются такими, какими их настроил пользователь. Winora их не трогает ни при
    /// применении схемы, ни при «заглушить всё».
    /// </para>
    /// </remarks>
    private static bool IsPreserved(string app, string eventName) =>
        app.StartsWith("sapisvr", StringComparison.OrdinalIgnoreCase)
        || eventName.StartsWith("Notification.Looping.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Makes Winora's set the whole scheme: its own sounds where it has one, silence everywhere
    /// else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Silencing the rest is the point, and it is what the owner asked for on 2026-08-09. Writing
    /// only the seven events Winora has sounds for left every other event playing its stock Windows
    /// sound, so a machine ended up with two sound designs at once — Winora's soft set for
    /// notifications and devices, the standard set for everything from a menu command to an empty
    /// recycle bin. That is not a theme, it is a mixture.
    /// </para>
    /// <para>
    /// Every registered application is walked, not just the shell's <c>.Default</c>: Explorer and
    /// others register their own events under <c>AppEvents\Schemes\Apps</c>, and leaving those
    /// untouched is exactly how the mixture came back on the screens that use them.
    /// </para>
    /// <para>
    /// Nothing is remembered to undo this. Windows keeps each event's stock sound in the
    /// <c>.Default</c> subkey beside the live <c>.Current</c> one, so
    /// <see cref="RestoreDefaults" /> copies back what the system itself recorded — which stays
    /// correct even if Winora's own state is lost entirely.
    /// </para>
    /// </remarks>
    public SoundApplyResult Apply(IReadOnlyDictionary<SoundEvent, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        // Имя события оболочки -> файл, но только там, где файл действительно есть.
        var ours = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keyName, soundEvent) in EventKeys)
        {
            if (files.TryGetValue(soundEvent, out var file) && File.Exists(file))
            {
                ours[keyName] = file;
            }
        }

        var applied = 0;
        var silenced = 0;
        var skipped = 0;

        foreach (var (app, eventName) in AllEvents())
        {
            if (IsPreserved(app, eventName))
            {
                continue;
            }

            var isOurs = string.Equals(app, DefaultApp, StringComparison.OrdinalIgnoreCase)
                && ours.ContainsKey(eventName);

            var value = isOurs ? ours[eventName] : string.Empty;

            if (!TryWriteCurrent(app, eventName, value))
            {
                skipped++;
            }
            else if (isOurs)
            {
                applied++;
            }
            else
            {
                silenced++;
            }
        }

        return new SoundApplyResult(applied, silenced, skipped);
    }

    /// <inheritdoc />
    public SoundApplyResult RestoreDefaults()
    {
        var applied = 0;
        var skipped = 0;

        // Every event, not only the ones Winora has sounds for. Apply touches all of them, so undo
        // has to as well; restoring seven and leaving fifty muted would be worse than not undoing.
        foreach (var (app, eventName) in AllEvents())
        {
            var stock = ReadValue($@"{AppsRoot}\{app}\{eventName}\.Default");
            if (stock is null || !TryWriteCurrent(app, eventName, stock))
            {
                skipped++;
                continue;
            }

            applied++;
        }

        return new SoundApplyResult(applied, 0, skipped);
    }

    /// <inheritdoc />
    public SoundApplyResult Silence()
    {
        var silenced = 0;
        var skipped = 0;

        foreach (var (app, eventName) in AllEvents())
        {
            // Будильники, входящие звонки и подтверждения распознавания речи не
            // глушатся даже здесь: см. IsPreserved.
            if (IsPreserved(app, eventName))
            {
                continue;
            }

            // An empty path is how Windows itself records "this event makes no sound", which is
            // what the shipped .None scheme contains.
            if (TryWriteCurrent(app, eventName, string.Empty))
            {
                silenced++;
            }
            else
            {
                skipped++;
            }
        }

        return new SoundApplyResult(0, silenced, skipped);
    }

    /// <summary>
    /// Every sound event Windows knows about, as (application, event).
    /// </summary>
    /// <remarks>
    /// Enumerated from the registry rather than listed in code. The set differs between machines —
    /// installed software adds its own applications and events — and a hard-coded list would go
    /// stale into exactly the half-applied state this class exists to avoid. Only events that
    /// already carry a <c>.Current</c> subkey are returned: that subkey is Windows' own record that
    /// the event is real, and creating one would be inventing an event.
    /// </remarks>
    private static IEnumerable<(string App, string Event)> AllEvents()
    {
        RegistryKey? apps = null;
        try
        {
            apps = Registry.CurrentUser.OpenSubKey(AppsRoot, writable: false);
        }
        catch (Exception)
        {
            yield break;
        }

        if (apps is null)
        {
            yield break;
        }

        using (apps)
        {
            string[] appNames;
            try
            {
                appNames = apps.GetSubKeyNames();
            }
            catch (Exception)
            {
                yield break;
            }

            foreach (var app in appNames)
            {
                foreach (var eventName in EventsOf(app))
                {
                    yield return (app, eventName);
                }
            }
        }
    }

    private static string[] EventsOf(string app)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{AppsRoot}\{app}", writable: false);
            if (key is null)
            {
                return [];
            }

            return key.GetSubKeyNames()
                .Where(name => HasCurrent(app, name))
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool HasCurrent(string app, string eventName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                $@"{AppsRoot}\{app}\{eventName}\.Current",
                writable: false);
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Diagnostic hook: called with (path, written, readBack) for the first few writes of a run.
    /// </summary>
    /// <remarks>
    /// The applier reported 49 successful writes while the real hive was untouched, and from outside
    /// the process there is no way to tell a write that went somewhere else from a write that never
    /// happened. Reading the value straight back closes that gap: if it comes back as what was
    /// written and the hive still disagrees, the write is being redirected; if it comes back as the
    /// old value, the write is silently failing.
    /// </remarks>
    public static Action<string, string, string?>? WriteObserver { get; set; }

    private static int _observed;

    private static bool TryWriteCurrent(string app, string eventKeyName, string value)
    {
        var path = $@"{AppsRoot}\{app}\{eventKeyName}\.Current";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            if (key is null)
            {
                return false;
            }

            // The default value of the .Current subkey is the sound path; Windows reads it on every
            // play, so nothing has to be told to reload.
            key.SetValue(string.Empty, value, RegistryValueKind.String);

            if (WriteObserver is { } observer && Interlocked.Increment(ref _observed) <= 3)
            {
                // Re-opened rather than read through the same handle, so a cached view cannot
                // answer for the hive.
                using var verify = Registry.CurrentUser.OpenSubKey(path, writable: false);
                observer(path, value, verify?.GetValue(string.Empty) as string);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Resets the per-run diagnostic sample counter.</summary>
    public static void BeginObservedRun() => Interlocked.Exchange(ref _observed, 0);

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
