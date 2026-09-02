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

    /// <summary>Who made the pack. Shown because somebody did.</summary>
    [ObservableProperty]
    public partial string Author { get; set; } = string.Empty;

    /// <summary>The catalogue entry this card came from, or null when the pack is only on disk.</summary>
    public CursorOfferView? Listing { get; init; }

    /// <summary>True once the pack's files are on this machine.</summary>
    [ObservableProperty]
    public partial bool IsDownloaded { get; set; }

    /// <summary>The card's own button: "Скачать" until it is here, "Применить" afterwards.</summary>
    [ObservableProperty]
    public partial string DownloadLabel { get; set; } = string.Empty;

    /// <summary>How large the download is, or empty for a pack already here.</summary>
    [ObservableProperty]
    public partial string SizeText { get; set; } = string.Empty;

    /// <summary>Where the card's picture comes from before anything has been downloaded.</summary>
    [ObservableProperty]
    public partial string PreviewUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; set; }
}

/// <summary>
/// The cursor packs already installed on this machine, applied through documented Win32 calls.
/// </summary>
/// <remarks>
/// <para>
/// Packs are offered from a catalogue Winora publishes beside itself, and the owner asked for that
/// on 2026-08-30. This used to say that Winora does not download packs at all, and the reason was
/// sound: an archive of arbitrary files from a stranger's site, unpacked by a program running as
/// administrator, is the shortest path from somebody's upload to code running on this machine.
/// </para>
/// <para>
/// That path is closed rather than accepted. The catalogue is Winora's own, not a third-party site,
/// and <see cref="ICursorCatalogue"/> takes only <c>.cur</c> and <c>.ani</c> out of an archive, each
/// by its bare file name — no scripts, no executables, no folder structure, and nothing that can
/// name a place outside the pack's own folder. What arrives is pointers or nothing.
/// </para>
/// <para>
/// Packs the owner drops into the folder themselves still appear, exactly as before, and are not
/// told apart from downloaded ones once they are there.
/// </para>
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

    private readonly ICursorDownloadService _catalogue;

    public CursorsViewModel(
        ICursorService cursors,
        ICursorDownloadService catalogue,
        ILocalizationService text)
    {
        _cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <summary>
    /// Builds the cards: what is here, plus what is on offer and not here yet.
    /// </summary>
    /// <remarks>
    /// The catalogue is asked for over the network and the folder is read from disk, so the whole
    /// list is built before anything is put into <see cref="Packs"/> — a page can be left at any
    /// await, and filling a collection WinUI has already torn down is what crashed the animations
    /// screen.
    /// </remarks>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Title = _text.Get("Nav_Cursors");
        Subtitle = _text.Get("Cursors_Subtitle");
        SessionNote = _text.Get("Cursors_SessionNote");
        RestoreLabel = _text.Get("Cursors_Restore");
        OpenFolderLabel = _text.Get("Cursors_OpenFolder");
        PackFolder = _cursors.PackFolder;

        var here = _cursors.Packs();
        var offered = await _catalogue.OffersAsync(cancellationToken).ConfigureAwait(true);

        var built = new List<CursorPackViewModel>();

        foreach (var pack in here)
        {
            // A downloaded pack sits in a folder named after its catalogue entry, which is how the
            // author and the entry are recovered for a card that is otherwise just files on disk.
            var listing = offered.FirstOrDefault(l =>
                string.Equals(l.Id, pack.FolderName, StringComparison.OrdinalIgnoreCase));

            built.Add(new CursorPackViewModel
            {
                Name = pack.Name,
                PreviewPath = pack.PreviewPath,
                ApplyLabel = _text.Get("Cursors_Apply"),
                Author = listing?.Author ?? string.Empty,
                Listing = listing,
                IsDownloaded = true,
            });
        }

        foreach (var listing in offered)
        {
            if (built.Any(card => card.Listing is { } known &&
                string.Equals(known.Id, listing.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            built.Add(new CursorPackViewModel
            {
                Name = listing.Name,
                Author = listing.Author,
                PreviewUrl = listing.PreviewUrl,
                Listing = listing,
                IsDownloaded = false,
                DownloadLabel = _text.Get("Cursors_Download"),
                SizeText = Megabytes(listing.SizeBytes),
            });
        }

        cancellationToken.ThrowIfCancellationRequested();

        Packs.Clear();

        foreach (var card in built)
        {
            Packs.Add(card);
        }

        if (Packs.Count == 0)
        {
            StatusMessage = _text.Get("Cursors_NoneInstalled");
        }
    }

    /// <summary>
    /// Fetches one pack, then rebuilds the list so the card becomes an ordinary installed one.
    /// </summary>
    /// <remarks>
    /// Reloading rather than flipping the card's own flag: what makes a pack usable is files on
    /// disk, and only a re-read of the folder knows whether they are there. A card that said
    /// "downloaded" because a download reported success would be repeating a claim instead of
    /// checking it.
    /// </remarks>
    public async Task DownloadAsync(CursorPackViewModel pack)
    {
        ArgumentNullException.ThrowIfNull(pack);

        if (pack.IsBusy || pack.Listing is not { } listing)
        {
            return;
        }

        pack.IsBusy = true;
        pack.Progress = 0;
        StatusMessage = string.Empty;

        try
        {
            var outcome = await _catalogue
                .DownloadAsync(listing.Id, new Progress<double>(value => pack.Progress = value))
                .ConfigureAwait(true);

            if (outcome != CursorDownloadResult.Installed)
            {
                StatusMessage = _text.Get(outcome switch
                {
                    CursorDownloadResult.DownloadFailed => "Cursors_DownloadFailed",
                    CursorDownloadResult.Unreadable => "Cursors_DownloadUnreadable",
                    CursorDownloadResult.Empty => "Cursors_DownloadEmpty",
                    _ => "Cursors_DownloadNotStored",
                });

                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = _text.Get("Cursors_DownloadFailed");
            return;
        }
        finally
        {
            pack.IsBusy = false;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    private static string Megabytes(long bytes) =>
        bytes <= 0
            ? string.Empty
            : (bytes / (1024d * 1024d)).ToString("0.#", CultureInfo.CurrentCulture) + " МБ";

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
