using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>One sound choice as a card.</summary>
public sealed partial class SoundChoiceViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Id { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApplyLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PreviewLabel { get; set; } = string.Empty;

    /// <summary>False for the Windows defaults, which have no single sound to stand for them.</summary>
    [ObservableProperty]
    public partial bool CanPreview { get; set; }

    /// <summary>True for the one choice styled as the primary action.</summary>
    [ObservableProperty]
    public partial bool IsPrimary { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// Exactly one of these is ever true, and neither while the pack is being applied — the
    /// progress ring takes the cell instead.
    /// </summary>
    public bool ShowPrimaryApply => IsPrimary && !IsBusy;

    public bool ShowSecondaryApply => !IsPrimary && !IsBusy;

    partial void OnIsPrimaryChanged(bool value) => RaiseApplyVisibility();

    partial void OnIsBusyChanged(bool value) => RaiseApplyVisibility();

    private void RaiseApplyVisibility()
    {
        OnPropertyChanged(nameof(ShowPrimaryApply));
        OnPropertyChanged(nameof(ShowSecondaryApply));
    }
}

/// <summary>
/// System sounds, replaced with tones Winora generates rather than files it downloads.
/// </summary>
/// <remarks>
/// Generating removes three problems at once: no archive from a stranger reaching an elevated app,
/// no licence to reason about, and the levels become something this project can tune rather than
/// inherit. The three packs are the same voice at three levels, because choosing a sound pack is
/// almost always choosing how much you want to be interrupted.
/// </remarks>
public sealed partial class SoundsViewModel : ObservableObject
{
    private readonly ISoundService _sounds;
    private readonly ILocalizationService _text;

    /// <summary>
    /// Модуль закрыт на технические работы.
    /// </summary>
    /// <remarks>
    /// Решение владельца от 2026-08-10. Применение схемы отчитывалось об успехе —
    /// набор опознан, шесть файлов найдено, девять записей сделано, ноль ошибок —
    /// а реестр при чтении снаружи оставался нетронутым. Причина не установлена:
    /// виртуализацию записи исключили, соседний домен панели задач в тот же вечер
    /// прошёл полный конвейер и его значение видно из непакетного процесса.
    ///
    /// Пока причина не найдена, экран не предлагает ничего применить. Действие с
    /// неизвестным результатом хуже отсутствия действия: человек считает звуки
    /// заменёнными, а они прежние.
    ///
    /// Снимается сменой этого значения на false — остальной код на месте, включая
    /// диагностику записи, ради которой всё и затевалось.
    /// </remarks>
    public bool IsUnderMaintenance => true;

    [ObservableProperty]
    public partial string MaintenanceTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MaintenanceNotice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FolderNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PackFolder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenFolderLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public ObservableCollection<SoundChoiceViewModel> Choices { get; } = [];

    public SoundsViewModel(ISoundService sounds, ILocalizationService text)
    {
        _sounds = sounds ?? throw new ArgumentNullException(nameof(sounds));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Title = _text.Get("Nav_Sounds");
        Subtitle = _text.Get("Sounds_Subtitle");
        FolderNote = _text.Get("Sounds_FolderNote");
        OpenFolderLabel = _text.Get("Sounds_OpenFolder");
        PackFolder = _sounds.PackFolder;
        MaintenanceTitle = _text.Get("Sounds_Maintenance_Title");
        MaintenanceNotice = _text.Get("Sounds_Maintenance");
        Choices.Clear();

        if (IsUnderMaintenance)
        {
            // Список не строится вовсе. Пустой экран честнее, чем список, из
            // которого ничего нельзя выбрать.
            return Task.CompletedTask;
        }

        foreach (var choice in _sounds.Choices())
        {
            Choices.Add(new SoundChoiceViewModel
            {
                Id = choice.Id,

                // A user-supplied pack names itself; the built-in choices have written copy.
                Name = choice.DisplayName.Length > 0
                    ? choice.DisplayName
                    : _text.Get($"Sounds_Pack_{choice.Id}"),
                Description = choice.DisplayName.Length > 0
                    ? _text.Get("Sounds_Pack_Folder_Detail")
                    : _text.Get($"Sounds_Pack_{choice.Id}_Detail"),
                // Возврат к стандартным звукам подписан своим словом.
                //
                // Раньше здесь у всех строк стояло «Применить», включая строку
                // «Стандартные Windows» — и её кнопка вдобавок нарисована
                // акцентной, то есть самой заметной на экране. Человек, желавший
                // применить набор, нажимал самую яркую кнопку и получал возврат
                // к звукам Windows. Отследили это по реестру 2026-08-10: после
                // трёх «применений» подряд состояние оставалось ровно исходным,
                // потому что каждый раз выполнялось восстановление.
                ApplyLabel = choice.Kind == SoundChoiceKind.WindowsDefaults
                    ? _text.Get("Sounds_Restore")
                    : _text.Get("Sounds_Apply"),
                PreviewLabel = _text.Get("Sounds_Preview"),
                CanPreview = choice.PreviewFile.Length > 0,

                // Returning to the Windows sounds is the one choice that always works, so it is
                // styled as the primary action rather than looking like one option among equals.
                IsPrimary = choice.Kind == SoundChoiceKind.WindowsDefaults,
            });
        }

        return Task.CompletedTask;
    }

    public void Preview(SoundChoiceViewModel choice)
    {
        ArgumentNullException.ThrowIfNull(choice);

        var source = _sounds.Choices().FirstOrDefault(c => string.Equals(c.Id, choice.Id, StringComparison.Ordinal));
        if (source is not null)
        {
            _sounds.Preview(source.PreviewFile);
        }
    }

    public async Task ApplyAsync(SoundChoiceViewModel choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (choice.IsBusy)
        {
            return;
        }

        choice.IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var source = _sounds.Choices()
                .FirstOrDefault(c => string.Equals(c.Id, choice.Id, StringComparison.Ordinal));
            if (source is null)
            {
                Diagnostics.DiagnosticSink.Note(
                    "Sounds.Apply",
                    $"choice '{choice.Id}' is no longer among the offered choices; nothing applied");
                return;
            }

            var outcome = await Task.Run(() => _sounds.Apply(source)).ConfigureAwait(true);

            // Recorded on every run, success included. Whether the registry was touched at all was
            // exactly the question three rounds of measurement could not answer from outside.
            Diagnostics.DiagnosticSink.Note(
                "Sounds.Apply",
                $"id='{source.Id}' kind={source.Kind} -> applied={outcome.Applied} skipped={outcome.Skipped}");

            // Silent on success, as elsewhere: the next notification is the confirmation. A run that
            // changed nothing is the only case worth a sentence.
            StatusMessage = outcome.Applied > 0 ? string.Empty : _text.Get("Sounds_NothingApplied");
        }
        finally
        {
            choice.IsBusy = false;
        }
    }
}
