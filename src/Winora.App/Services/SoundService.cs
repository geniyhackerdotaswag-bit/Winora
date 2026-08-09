using Winora.System.Windows;

namespace Winora.App.Services;

/// <summary>What a sound choice does when picked.</summary>
public enum SoundChoiceKind
{
    /// <summary>Point the events at a pack the user dropped in the folder.</summary>
    FolderPack,

    /// <summary>Put back the sounds Windows recorded for each event.</summary>
    WindowsDefaults,
}

/// <param name="Id">Stable identifier, also the folder name for a pack.</param>
/// <param name="Kind">What choosing it does.</param>
/// <param name="PreviewFile">The sound the preview button plays, or empty when there is none.</param>
/// <param name="DisplayName">Name for a user-supplied pack; empty for the built-in choices.</param>
/// <remarks>Carries no picture, for the same reason as <see cref="CursorPackView" />.</remarks>
public sealed record SoundChoiceView(
    string Id,
    SoundChoiceKind Kind,
    string PreviewFile,
    string DisplayName = "");

/// <summary>Lists and applies sound choices without a view model touching <c>Winora.System</c>.</summary>
public interface ISoundService
{
    string PackFolder { get; }

    IReadOnlyList<SoundChoiceView> Choices();

    /// <summary>Plays one sound so the user can hear it before changing anything.</summary>
    void Preview(string file);

    SoundApplyOutcome Apply(SoundChoiceView choice);
}

/// <param name="Applied">Events whose sound changed.</param>
/// <param name="Skipped">Events that could not be changed.</param>
public readonly record struct SoundApplyOutcome(int Applied, int Skipped);

/// <inheritdoc />
public sealed class SoundService : ISoundService
{
    private readonly ISoundPackBuilder _builder;
    private readonly ISoundFolderScanner _folder;
    private readonly ISoundSchemeApplier _applier;
    private readonly ISoundPlayer _player;

    public SoundService(
        ISoundPackBuilder builder,
        ISoundFolderScanner folder,
        ISoundSchemeApplier applier,
        ISoundPlayer player)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _player = player ?? throw new ArgumentNullException(nameof(player));
    }

    public string PackFolder => _builder.RootDirectory;

    public IReadOnlyList<SoundChoiceView> Choices()
    {
        var choices = new List<SoundChoiceView>();

        // Winora's own generated packs are gone by owner decision on 2026-08-04: synthesized tones
        // were never as good as a real set, and three of them crowded out the ones the user had
        // actually chosen. Their folders stay on disk — one of them may be the scheme currently
        // applied, and deleting the files would leave Windows pointing at nothing — and
        // SoundPackBuilder.Definitions still names them so the folder scan keeps skipping them
        // rather than offering them back as if the user had supplied them.
        foreach (var pack in _folder.Packs(_builder.RootDirectory))
        {
            choices.Add(new SoundChoiceView(
                Path.GetFileName(pack.Directory),
                SoundChoiceKind.FolderPack,
                pack.Files.TryGetValue(SoundEvent.Notification, out var notify)
                    ? notify
                    : pack.Files.Values.FirstOrDefault() ?? string.Empty,
                pack.Name));
        }

        // Last on purpose, and the screen styles it as the primary action: whatever else is on the
        // page, going back to what Windows shipped is the choice that always works and the one a
        // user reaches for when an experiment did not suit them.
        choices.Add(new SoundChoiceView("windows", SoundChoiceKind.WindowsDefaults, string.Empty));
        return choices;
    }

    public void Preview(string file)
    {
        if (!string.IsNullOrWhiteSpace(file))
        {
            _player.Play(file);
        }
    }

    public SoundApplyOutcome Apply(SoundChoiceView choice)
    {
        ArgumentNullException.ThrowIfNull(choice);

        var result = choice.Kind switch
        {
            SoundChoiceKind.WindowsDefaults => _applier.RestoreDefaults(),

            // Files are re-read from their source rather than carried on the view, so no caller can
            // point the applier at an arbitrary path.
            _ => _applier.Apply(
                _folder.Packs(_builder.RootDirectory)
                    .FirstOrDefault(pack => string.Equals(
                        Path.GetFileName(pack.Directory), choice.Id, StringComparison.Ordinal))
                    ?.Files ?? new Dictionary<SoundEvent, string>()),
        };

        return new SoundApplyOutcome(result.Applied, result.Skipped);
    }
}
