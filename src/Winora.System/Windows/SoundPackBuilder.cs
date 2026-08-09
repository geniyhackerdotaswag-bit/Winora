namespace Winora.System.Windows;

/// <summary>The sound events Winora replaces. Deliberately the few a user actually hears.</summary>
/// <remarks>
/// Windows registers 59 events and ships sounds for a handful. Filling all 59 would make the desktop
/// noisier, not nicer, so Winora writes only the events that are already audible by default and
/// leaves the rest silent as Windows does.
/// </remarks>
public enum SoundEvent
{
    /// <summary>The general notification, by far the most frequently heard.</summary>
    Notification,

    DeviceConnect,

    DeviceDisconnect,

    DeviceFail,

    LowBattery,

    CriticalBattery,

    Mail,
}

/// <param name="Id">Stable identifier used for the folder name.</param>
/// <param name="Gain">Master level for every sound in the pack, 0 to 1.</param>
public sealed record SoundPackDefinition(string Id, double Gain);

/// <summary>Builds the generated sound packs on disk.</summary>
public interface ISoundPackBuilder
{
    string RootDirectory { get; }

    /// <summary>The packs Winora offers, generating any that are missing.</summary>
    IReadOnlyList<string> EnsurePacks();

    /// <summary>Absolute paths for one pack, by event.</summary>
    IReadOnlyDictionary<SoundEvent, string> FilesFor(string packId);
}

/// <inheritdoc />
public sealed class SoundPackBuilder : ISoundPackBuilder
{
    /// <summary>
    /// Three levels of the same voice rather than three different characters. A user choosing a
    /// sound pack is almost always choosing how much they want to be interrupted, not what timbre
    /// they prefer.
    /// </summary>
    public static readonly IReadOnlyList<SoundPackDefinition> Definitions =
    [
        new("soft", 0.55),
        new("quiet", 0.28),
        new("faint", 0.14),
    ];

    private static readonly IReadOnlyDictionary<SoundEvent, SoundTone[]> Voices =
        new Dictionary<SoundEvent, SoundTone[]>
        {
            // A gentle rising third. Short enough not to overlap the next notification.
            [SoundEvent.Notification] =
            [
                new(587.33, 587.33, 0.12, 0.9),
                new(739.99, 739.99, 0.20, 0.8, 0.09),
            ],

            // Connect rises, disconnect falls: the pair should be legible without looking.
            [SoundEvent.DeviceConnect] =
            [
                new(523.25, 523.25, 0.10, 0.85),
                new(783.99, 783.99, 0.18, 0.75, 0.08),
            ],
            [SoundEvent.DeviceDisconnect] =
            [
                new(783.99, 783.99, 0.10, 0.85),
                new(523.25, 523.25, 0.18, 0.75, 0.08),
            ],

            // Lower and slightly longer. Attention without alarm: a harsh error tone is the one
            // sound people disable first, which loses the signal entirely.
            [SoundEvent.DeviceFail] =
            [
                new(392.00, 392.00, 0.14, 0.9),
                new(311.13, 311.13, 0.26, 0.85, 0.12),
            ],

            [SoundEvent.LowBattery] =
            [
                new(466.16, 466.16, 0.12, 0.7),
                new(392.00, 392.00, 0.22, 0.65, 0.10),
            ],
            [SoundEvent.CriticalBattery] =
            [
                new(466.16, 466.16, 0.10, 0.9),
                new(392.00, 392.00, 0.10, 0.9, 0.09),
                new(311.13, 311.13, 0.24, 0.85, 0.18),
            ],

            [SoundEvent.Mail] =
            [
                new(659.25, 659.25, 0.10, 0.7),
                new(880.00, 880.00, 0.18, 0.6, 0.08),
            ],
        };

    public SoundPackBuilder()
        : this(DefaultRoot())
    {
    }

    public SoundPackBuilder(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = rootDirectory;
    }

    public string RootDirectory { get; }

    /// <summary>
    /// Beside the cursor folder and for the same reason: a packaged app's own storage is redirected
    /// somewhere the user cannot find, and these are files they may well want to hear or replace.
    /// </summary>
    private static string DefaultRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Winora",
            "Sounds");

    public IReadOnlyList<string> EnsurePacks()
    {
        var built = new List<string>();

        foreach (var definition in Definitions)
        {
            var directory = Path.Combine(RootDirectory, definition.Id);
            try
            {
                Directory.CreateDirectory(directory);

                foreach (var (soundEvent, tones) in Voices)
                {
                    var file = Path.Combine(directory, soundEvent + ".wav");

                    // Regenerated only when absent, so a user who replaced one with their own audio
                    // keeps it.
                    if (!File.Exists(file))
                    {
                        File.WriteAllBytes(file, SoundSynth.Render(tones, definition.Gain));
                    }
                }

                built.Add(definition.Id);
            }
            catch (Exception)
            {
                // A pack that cannot be written is simply not offered.
            }
        }

        return built;
    }

    public IReadOnlyDictionary<SoundEvent, string> FilesFor(string packId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);

        var directory = Path.Combine(RootDirectory, packId);
        var files = new Dictionary<SoundEvent, string>();

        foreach (var soundEvent in Voices.Keys)
        {
            var file = Path.Combine(directory, soundEvent + ".wav");
            if (File.Exists(file))
            {
                files[soundEvent] = file;
            }
        }

        return files;
    }
}
