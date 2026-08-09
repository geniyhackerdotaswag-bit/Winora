using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>One detail row in the selected panel.</summary>
public sealed partial class StatViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;
}

/// <summary>
/// One component of the machine: its recent history, its current reading and its details.
/// </summary>
public sealed partial class PerformancePanelViewModel : ObservableObject
{
    /// <summary>
    /// A minute at one sample a second, the window Task Manager shows.
    /// </summary>
    private const int HistoryLength = 60;

    private readonly List<double> _history = [];

    public string Key { get; init; } = string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    partial void OnSubtitleChanged(string value) => OnPropertyChanged(nameof(HasSubtitle));

    [ObservableProperty]
    public partial string Reading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasPercent { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>
    /// A copy per update, because the chart only repaints when the property changes identity.
    /// Mutating one list in place would move the numbers and leave the line untouched.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<double> History { get; set; } = [];

    public ObservableCollection<StatViewModel> Stats { get; } = [];

    /// <summary>
    /// Adds a reading and drops the oldest once the window is full.
    /// </summary>
    /// <remarks>
    /// A panel with no percentage still keeps a history at zero, so the chart area stays the same
    /// height as its neighbours instead of the row collapsing.
    /// </remarks>
    public void Push(double? percent)
    {
        _history.Add(percent ?? 0);
        if (_history.Count > HistoryLength)
        {
            _history.RemoveAt(0);
        }

        History = _history.ToArray();
    }
}

/// <summary>
/// The performance screen: a list of components on the left, the selected one in detail on the right.
/// </summary>
/// <remarks>
/// <para>
/// The shape is Task Manager's on purpose, because it is the arrangement people already know and it
/// answers the question they arrive with — which part of the machine is busy. An earlier version
/// laid everything out as equal tiles with no history, which showed the same numbers and none of the
/// meaning: a spike a second ago looked identical to steady load.
/// </para>
/// <para>
/// Panels are updated in place, matched by key. Rebuilding the list every second would restart the
/// chart, lose the selection and make the whole page flicker.
/// </para>
/// </remarks>
public sealed partial class PerformanceViewModel : ObservableObject
{
    private readonly IPerformanceService _performance;
    private readonly ILocalizationService _text;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    [ObservableProperty]
    public partial PerformancePanelViewModel? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    partial void OnSelectedChanged(PerformancePanelViewModel? value)
    {
        foreach (var panel in Panels)
        {
            panel.IsSelected = ReferenceEquals(panel, value);
        }

        OnPropertyChanged(nameof(HasSelection));
    }

    public ObservableCollection<PerformancePanelViewModel> Panels { get; } = [];

    public PerformanceViewModel(IPerformanceService performance, ILocalizationService text)
    {
        _performance = performance ?? throw new ArgumentNullException(nameof(performance));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Title = _text.Get("Nav_Performance");
        Subtitle = _text.Get("Performance_Subtitle");

        Refresh();

        // The processor first, matching where a person looks when they open this screen.
        Selected ??= Panels.FirstOrDefault();

        return Task.CompletedTask;
    }

    public void Select(PerformancePanelViewModel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        Selected = panel;
    }

    /// <summary>Takes one sample. Called on a timer by the page.</summary>
    public void Refresh()
    {
        var sampled = _performance.Sample();

        for (var index = 0; index < sampled.Count; index++)
        {
            var view = sampled[index];
            var panel = Panels.FirstOrDefault(p => string.Equals(p.Key, view.Key, StringComparison.Ordinal));

            if (panel is null)
            {
                panel = new PerformancePanelViewModel { Key = view.Key };
                Panels.Insert(Math.Min(index, Panels.Count), panel);
            }

            panel.Title = view.TitleKey.Length > 0 && view.Name == view.TitleKey
                ? _text.Get(view.TitleKey)
                : view.Name;

            panel.Subtitle = view.Subtitle;
            panel.Reading = view.Reading;
            panel.HasPercent = view.Percent is not null;
            panel.Push(view.Percent);

            UpdateStats(panel, view.Stats);
        }

        // A drive unplugged or an adapter taken down stops being reported, so its panel goes too.
        var keys = sampled.Select(static view => view.Key).ToHashSet(StringComparer.Ordinal);
        for (var index = Panels.Count - 1; index >= 0; index--)
        {
            if (!keys.Contains(Panels[index].Key))
            {
                if (ReferenceEquals(Selected, Panels[index]))
                {
                    Selected = null;
                }

                Panels.RemoveAt(index);
            }
        }

        Selected ??= Panels.FirstOrDefault();
    }

    /// <remarks>
    /// Rows are reused where the count matches, which is the normal case — only the values move.
    /// Rebuilding them each second made the detail panel flicker once a second.
    /// </remarks>
    private void UpdateStats(PerformancePanelViewModel panel, IReadOnlyList<StatView> stats)
    {
        while (panel.Stats.Count > stats.Count)
        {
            panel.Stats.RemoveAt(panel.Stats.Count - 1);
        }

        for (var index = 0; index < stats.Count; index++)
        {
            var stat = stats[index];

            // A value can itself be a resource key, for the yes/no rows.
            var value = stat.Value.StartsWith("Performance_Value_", StringComparison.Ordinal)
                ? _text.Get(stat.Value)
                : stat.Value;

            if (index < panel.Stats.Count)
            {
                panel.Stats[index].Label = _text.Get(stat.Label);
                panel.Stats[index].Value = value;
                continue;
            }

            panel.Stats.Add(new StatViewModel { Label = _text.Get(stat.Label), Value = value });
        }
    }

}
