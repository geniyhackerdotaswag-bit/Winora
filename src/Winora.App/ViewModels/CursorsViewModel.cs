using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>One installed cursor pack, as a card.</summary>
public sealed partial class CursorPackViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>Path to the pack's normal-select cursor, used as the card's preview image.</summary>
    [ObservableProperty]
    public partial string PreviewPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApplyLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsApplied { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }
}

/// <summary>
/// The cursor packs already installed on this machine, applied through documented Win32 calls.
/// </summary>
/// <remarks>
/// Winora does not download packs. A cursor pack is an archive of arbitrary files from a third-party
/// site, this app runs elevated, and fetching one and installing it would be the shortest path from
/// a stranger's upload to administrative code on the user's machine. Packs the user installs
/// themselves appear here automatically, because Windows registers them as schemes.
/// </remarks>
public sealed partial class CursorsViewModel : ObservableObject
{
    private readonly ICursorService _cursors;
    private readonly ILocalizationService _text;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    /// <summary>States that a change lasts until sign-out. Shown before anything is applied.</summary>
    [ObservableProperty]
    public partial string SessionNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RestoreLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenFolderLabel { get; set; } = string.Empty;

    /// <summary>Where to drop packs. Shown so the folder is findable without hunting.</summary>
    [ObservableProperty]
    public partial string PackFolder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public ObservableCollection<CursorPackViewModel> Packs { get; } = [];

    public CursorsViewModel(ICursorService cursors, ILocalizationService text)
    {
        _cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Title = _text.Get("Nav_Cursors");
        Subtitle = _text.Get("Cursors_Subtitle");
        SessionNote = _text.Get("Cursors_SessionNote");
        RestoreLabel = _text.Get("Cursors_Restore");
        OpenFolderLabel = _text.Get("Cursors_OpenFolder");
        PackFolder = _cursors.PackFolder;
        Packs.Clear();

        foreach (var pack in _cursors.Packs())
        {
            Packs.Add(new CursorPackViewModel
            {
                Name = pack.Name,
                PreviewPath = pack.PreviewPath,
                ApplyLabel = _text.Get("Cursors_Apply"),
            });
        }

        if (Packs.Count == 0)
        {
            StatusMessage = _text.Get("Cursors_NoneInstalled");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies a pack. The report says how many cursors actually changed rather than claiming the
    /// pack was installed: several roles have no documented way to be set, and a pack missing a file
    /// leaves that cursor as it was.
    /// </summary>
    public async Task ApplyAsync(CursorPackViewModel pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.IsBusy)
        {
            return;
        }

        pack.IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var outcome = await Task.Run(() => _cursors.Apply(pack.Name)).ConfigureAwait(true);

            // Silent on success: the cursor on screen has already changed, which is a better
            // confirmation than a sentence. Only a run that changed nothing needs saying.
            StatusMessage = outcome.Applied > 0 ? string.Empty : _text.Get("Cursors_NothingApplied");

            foreach (var candidate in Packs)
            {
                candidate.IsApplied = ReferenceEquals(candidate, pack) && outcome.Applied > 0;
            }
        }
        finally
        {
            pack.IsBusy = false;
        }
    }

    /// <summary>Puts back whatever Windows has on record, which undoes any pack applied here.</summary>
    public async Task RestoreAsync()
    {
        StatusMessage = string.Empty;
        var restored = await Task.Run(_cursors.Restore).ConfigureAwait(true);

        StatusMessage = restored
            ? _text.Get("Cursors_Restored")
            : _text.Get("Cursors_RestoreFailed");

        foreach (var pack in Packs)
        {
            pack.IsApplied = false;
        }
    }
}
