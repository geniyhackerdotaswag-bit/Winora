using Winora.System.Windows;

namespace Winora.App.Services;

/// <param name="Name">The pack name.</param>
/// <param name="RoleCount">How many cursor roles the pack defines.</param>
/// <param name="PreviewPath">The pack's normal-select cursor, or empty when it has none.</param>
/// <remarks>
/// Carries no banner picture on purpose. Pack art was shown here once and was wide marketing
/// imagery: at card size it squeezed the name and pushed the button out of the card. The card shows
/// the pack's own pointer instead, which is the thing being chosen.
/// </remarks>
public sealed record CursorPackView(
    string Name,
    int RoleCount,
    string PreviewPath);

/// <param name="Applied">Cursors Windows accepted.</param>
/// <param name="Skipped">Cursors that could not be set.</param>
public readonly record struct CursorApplyOutcome(int Applied, int Skipped);

/// <summary>
/// Lists and applies cursor packs for the presentation layer, without letting a ViewModel reference
/// <c>Winora.System</c> directly.
/// </summary>
public interface ICursorService
{
    IReadOnlyList<CursorPackView> Packs();

    /// <summary>The folder the user drops packs into, so the screen can offer to open it.</summary>
    string PackFolder { get; }

    CursorApplyOutcome Apply(string packName);

    bool Restore();
}

/// <inheritdoc />
public sealed class CursorService : ICursorService
{
    private readonly ICursorFolderScanner _folder;
    private readonly ICursorApplier _applier;

    public CursorService(ICursorFolderScanner folder, ICursorApplier applier)
    {
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
    }

    public string PackFolder => _folder.RootDirectory;

    /// <remarks>
    /// Only packs from the user's folder. The schemes Windows ships were listed at first and were
    /// simply noise: a dozen cards for cursor sets the user never chose and would pick from the
    /// Windows settings anyway, crowding out the ones they had actually added.
    /// </remarks>
    public IReadOnlyList<CursorPackView> Packs() =>
        _folder.Packs()
            .Select(static pack => new CursorPackView(
                pack.Name,
                pack.Files.Count,
                // The normal-select cursor stands for the pack: it is what the user is choosing.
                pack.Files.TryGetValue(CursorRole.Arrow, out var arrow) ? arrow : string.Empty))
            .ToArray();

    public CursorApplyOutcome Apply(string packName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packName);

        // Resolved by re-reading the folder rather than taking a path from the caller, so no caller
        // can hand the applier an arbitrary file.
        var files = _folder.Packs()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, packName, StringComparison.Ordinal))
            ?.Files;

        if (files is null)
        {
            return new CursorApplyOutcome(0, 0);
        }

        var result = _applier.Apply(files);
        return new CursorApplyOutcome(result.Applied, result.Skipped);
    }

    public bool Restore() => _applier.Restore();
}
