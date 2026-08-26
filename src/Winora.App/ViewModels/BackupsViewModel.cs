using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>One snapshot of Winora's own records.</summary>
public sealed partial class StateBackupViewModel : ObservableObject
{
    public string BackupId { get; init; } = string.Empty;

    [ObservableProperty]
    public partial string When { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Verified { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RestoreLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool CanRestore => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanRestore));
}

/// <summary>
/// Snapshots that protect the ability to undo.
/// </summary>
/// <remarks>
/// The naming is the trap here. "Backups" reads as "copies of my Windows settings", and these are
/// not that: they are copies of Winora's own journal, plan archive and per-change backups — the
/// bookkeeping that makes a change reversible at all. If that is lost, changes already made to
/// Windows become permanent whether or not the user wanted them. The screen leads with that
/// distinction rather than burying it, because a user who restores one expecting their desktop to
/// change will conclude the feature is broken.
/// </remarks>
public sealed partial class BackupsViewModel : ObservableObject
{
    private readonly IStateBackupService _backups;
    private readonly ILocalizationService _text;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CreateLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RefreshLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool CanCreate => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanCreate));

    public ObservableCollection<StateBackupViewModel> Backups { get; } = [];

    public BackupsViewModel(IStateBackupService backups, ILocalizationService text)
    {
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Backups");
        Subtitle = _text.Get("Backups_Subtitle");
        CreateLabel = _text.Get("Backups_Create");
        RefreshLabel = _text.Get("Backups_Refresh");
        EmptyMessage = _text.Get("Backups_Empty");

        await ReloadAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        Backups.Clear();

        var entries = await _backups.ReadAsync(cancellationToken).ConfigureAwait(true);

        foreach (var entry in entries)
        {
            Backups.Add(new StateBackupViewModel
            {
                BackupId = entry.BackupId,
                When = entry.CreatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                Verified = _text.Get(entry.IsVerified ? "Backups_Verified" : "Backups_Unverified"),
                RestoreLabel = _text.Get("Backups_Restore"),
            });
        }

        IsEmpty = Backups.Count == 0;
    }

    public async Task CreateAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var created = await _backups.CreateAsync().ConfigureAwait(true);

            StatusMessage = created
                ? _text.Get("Backups_Created")
                : _text.Get("Backups_CreateFailed");

            await ReloadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <remarks>
    /// Restoring replaces Winora's current records with the snapshot's. Windows is not touched, and
    /// the message says so afterwards — otherwise a user watching their desktop for a change would
    /// think nothing happened.
    /// </remarks>
    public async Task RestoreAsync(StateBackupViewModel backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (backup.IsBusy)
        {
            return;
        }

        backup.IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var restored = await _backups.RestoreAsync(backup.BackupId).ConfigureAwait(true);

            StatusMessage = restored
                ? _text.Get("Backups_Restored")
                : _text.Get("Backups_RestoreFailed");

            await ReloadAsync().ConfigureAwait(true);
        }
        finally
        {
            backup.IsBusy = false;
        }
    }
}
