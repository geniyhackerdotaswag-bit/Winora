namespace Winora.System.Windows;

/// <param name="Name">Readable pack name.</param>
/// <param name="Directory">Absolute path to the pack folder.</param>
/// <param name="Files">Event to sound file, for events that could be identified.</param>
/// <param name="ImagePath">The pack's own preview image, or empty.</param>
public sealed record SoundFolderPack(
    string Name,
    string Directory,
    IReadOnlyDictionary<SoundEvent, string> Files,
    string ImagePath);

/// <summary>Reads sound packs the user dropped into the sound folder.</summary>
public interface ISoundFolderScanner
{
    IReadOnlyList<SoundFolderPack> Packs(string rootDirectory);
}

/// <summary>
/// Reads sound packs from the user's folder, unpacking archives on the way.
/// </summary>
/// <remarks>
/// <para>
/// Only <c>.wav</c> is extracted and read. Downloaded sound packs routinely ship <c>.reg</c> files
/// that apply the scheme by importing registry commands somebody else wrote; Winora will not open
/// or apply them, which matters more because this app runs elevated.
/// </para>
/// <para>
/// Events are matched by file name, which is a guess. The ordering below is the guard: "disconnect"
/// contains "connect", so the longer token has to win first. That is the same trap that would have
/// classified a cursor pack's "normal" pointer as "no".
/// </para>
/// </remarks>
public sealed class SoundFolderScanner : ISoundFolderScanner
{
    private static readonly (string Token, SoundEvent Event)[] Tokens =
    [
        // Longest and most specific first.
        ("disconnect", SoundEvent.DeviceDisconnect),
        ("usb off", SoundEvent.DeviceDisconnect),
        ("usb error", SoundEvent.DeviceFail),
        ("device fail", SoundEvent.DeviceFail),
        ("critical", SoundEvent.CriticalBattery),
        ("low batt", SoundEvent.LowBattery),
        ("battery", SoundEvent.LowBattery),

        ("connect", SoundEvent.DeviceConnect),
        ("usb on", SoundEvent.DeviceConnect),
        ("insert", SoundEvent.DeviceConnect),
        ("remove", SoundEvent.DeviceDisconnect),

        // Диалог критической ошибки. Токены нарочно точные: общее "error" ниже
        // означает отказ устройства и должно остаться за ним.
        ("critical stop", SoundEvent.Error),
        ("hand", SoundEvent.Error),
        ("oshib", SoundEvent.Error),
        ("device fail", SoundEvent.DeviceFail),

        ("mail", SoundEvent.Mail),

        // Диалоги и системные сообщения.
        ("exclamation", SoundEvent.Warning),
        ("warning", SoundEvent.Warning),
        ("preduprezh", SoundEvent.Warning),

        ("asterisk", SoundEvent.Information),
        ("information", SoundEvent.Information),
        ("info", SoundEvent.Information),

        ("uac", SoundEvent.Elevation),
        ("elevation", SoundEvent.Elevation),
        ("admin", SoundEvent.Elevation),

        ("reminder", SoundEvent.Reminder),
        ("calendar", SoundEvent.Reminder),
        ("napomin", SoundEvent.Reminder),

        ("message", SoundEvent.Message),
        ("nudge", SoundEvent.Message),
        ("chat", SoundEvent.Message),
        ("sms", SoundEvent.Message),

        ("logon", SoundEvent.Logon),
        ("logoff", SoundEvent.Logon),
        ("unlock", SoundEvent.Logon),
        ("startup", SoundEvent.Logon),
        ("vhod", SoundEvent.Logon),

        // Общее "error" по-прежнему означает отказ устройства, хотя теперь есть
        // и отдельное событие Error. Менять это нельзя: в существующих наборах
        // файл с таким именем уже звучит на отказе устройства, и переназначение
        // молча переставило бы звук у тех, кто ничего не менял. Для новой
        // ошибки-диалога есть более точные токены выше.
        ("error", SoundEvent.DeviceFail),
        ("fail", SoundEvent.DeviceFail),

        // The general notification, written every way there is including transliterated Russian.
        ("notification", SoundEvent.Notification),
        ("notify", SoundEvent.Notification),
        ("balloon", SoundEvent.Notification),
        ("default", SoundEvent.Notification),
        ("uved", SoundEvent.Notification),
    ];

    private static readonly string[] SoundExtensions = [".wav"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];

    private readonly IArchiveExtractor _extractor;

    public SoundFolderScanner()
        : this(new ArchiveExtractor(".wav", ".jpg", ".jpeg", ".png"))
    {
    }

    public SoundFolderScanner(IArchiveExtractor extractor)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    }

    /// <summary>
    /// Resolves a sound file name to an event, or null when nothing matches. Public because the
    /// matching is the guess in this class and deserves direct testing.
    /// </summary>
    public static SoundEvent? EventForFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        foreach (var (token, soundEvent) in Tokens)
        {
            if (name.Contains(token, StringComparison.Ordinal))
            {
                return soundEvent;
            }
        }

        return null;
    }

    public IReadOnlyList<SoundFolderPack> Packs(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        try
        {
            Directory.CreateDirectory(rootDirectory);
        }
        catch (Exception)
        {
            return [];
        }

        _extractor.ExtractPending(rootDirectory);

        var packs = new List<SoundFolderPack>();
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(rootDirectory);
        }
        catch (Exception)
        {
            return [];
        }

        foreach (var directory in directories)
        {
            // Winora's own generated packs live here too and are offered separately, so they are
            // not read a second time as if the user had supplied them.
            if (SoundPackBuilder.Definitions.Any(definition =>
                    string.Equals(definition.Id, Path.GetFileName(directory), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var pack = TryRead(directory);
            if (pack is not null)
            {
                packs.Add(pack);
            }
        }

        return packs
            .OrderBy(static pack => pack.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static SoundFolderPack? TryRead(string directory)
    {
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception)
        {
            return null;
        }

        var sounds = files
            .Where(static file => SoundExtensions.Contains(
                Path.GetExtension(file),
                StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (sounds.Length == 0)
        {
            return null;
        }

        var candidates = new Dictionary<SoundEvent, List<string>>();
        foreach (var file in sounds)
        {
            if (EventForFileName(file) is not { } soundEvent)
            {
                continue;
            }

            if (!candidates.TryGetValue(soundEvent, out var list))
            {
                list = [];
                candidates[soundEvent] = list;
            }

            list.Add(file);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Shortest name wins, as with cursors: packs ship variants and the plain one is the shortest.
        var matched = candidates.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value
                .OrderBy(static file => Path.GetFileName(file).Length)
                .ThenBy(static file => file, StringComparer.Ordinal)
                .First());

        var image = files.FirstOrDefault(static file => ImageExtensions.Contains(
            Path.GetExtension(file),
            StringComparer.OrdinalIgnoreCase)) ?? string.Empty;

        var name = CursorPackNaming.Clean(NameSourceFor(directory));
        return new SoundFolderPack(name, directory, matched, image);
    }

    private static string NameSourceFor(string directory)
    {
        var folderName = Path.GetFileName(directory);
        try
        {
            var children = Directory.GetDirectories(directory);
            if (children.Length == 1 && Directory.GetFiles(directory).Length == 0)
            {
                return Path.GetFileName(children[0]);
            }
        }
        catch (Exception)
        {
        }

        return folderName;
    }
}
