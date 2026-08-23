namespace Winora.System.Windows;

/// <summary>The sound events a pack can fill.</summary>
/// <remarks>
/// <para>
/// Measured on a real machine 2026-08-09: Windows registers 75 events and ships a sound for 49 of
/// them. These are the ones a person actually hears in a day. <c>SoundSchemeApplier</c> maps each to
/// the Windows event names that mean the same thing and silences everything else, so the desktop
/// speaks with one voice rather than two.
/// </para>
/// <para>
/// One member per meaning, not per event. A chat message and an SMS notification are the same thing
/// to a listener, and giving each its own sound would teach nothing.
/// </para>
/// <para>
/// <strong>Only a pack supplies these.</strong> <see cref="SoundPackBuilder" /> can synthesize seven
/// of them and no more, which is deliberate — see the remarks on its voice table. A member with no
/// generated tone is not an omission: it is filled by the folder a user chose, or by nothing.
/// </para>
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

    /// <summary>An information dialog, and the generic system notice beside it.</summary>
    Information,

    /// <summary>A warning dialog: something needs attention but nothing has failed.</summary>
    Warning,

    /// <summary>A critical stop. The only sound in the set allowed to be unpleasant.</summary>
    Error,

    /// <summary>A message from a person — chat, SMS, a nudge.</summary>
    Message,

    /// <summary>A calendar or task reminder.</summary>
    Reminder,

    /// <summary>The elevation prompt. Distinct on purpose: it asks for a decision.</summary>
    Elevation,

    /// <summary>Signing in, and unlocking the session.</summary>
    Logon,
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

    /// <summary>
    /// The synthesized voices. Seven, and it stays seven.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Do not extend this to cover the newer members of <see cref="SoundEvent" />. Generated packs
    /// were withdrawn by owner decision on 2026-08-04 — synthesized tones never matched a real set
    /// and the three of them crowded out the packs a user had actually chosen — so
    /// <see cref="EnsurePacks" /> is called from nowhere and these tones are never rendered.
    /// </para>
    /// <para>
    /// Extending it was tried on 2026-08-09 and reverted the same day: seven more voices were
    /// written for the new events before anyone checked whether the generator still ran. It does
    /// not. The folders on disk are leftovers from before the decision, kept only so the folder scan
    /// keeps skipping them rather than offering them back as if the user had supplied them.
    /// </para>
    /// <para>
    /// Coverage beyond these seven comes from the event mapping in <c>SoundSchemeApplier</c> and the
    /// file-name tokens in <c>SoundFolderScanner</c>, which is where the work belongs.
    /// </para>
    /// </remarks>
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
