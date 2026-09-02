using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>One entry in the audit trail.</summary>
public sealed partial class ActionRecordViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Category { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Risk { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string When { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool NeededAdministrator { get; set; }

    [ObservableProperty]
    public partial string AdministratorLabel { get; set; } = string.Empty;

    /// <summary>
    /// How many things the entry touched, already in words, or empty when it counts in nothing.
    /// </summary>
    /// <remarks>
    /// A count is neither a path nor a value nor a name, so it keeps this screen safe to share
    /// while telling the one thing the rest of the row cannot: whether anything happened. Four
    /// cleanup entries minutes apart read identically here, and one of them had removed nothing.
    /// </remarks>
    [ObservableProperty]
    public partial string Affected { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasAffected { get; set; }
}

/// <summary>
/// The audit trail: what Winora did, deliberately without saying to what.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from the changes screen on purpose. That one lists changes so they can be undone, and
/// names them. This one is the record that stays safe to share: category, outcome, risk and rights,
/// and no path, value or name anywhere. A trail that leaked what the user changed would be one they
/// could not send to anybody when something went wrong.
/// </para>
/// <para>
/// The journal was empty on every machine until now — the writer existed and was never called. It is
/// wired at the apply and rollback points, so entries appear from the next change onwards; earlier
/// ones cannot be reconstructed and the screen does not pretend otherwise.
/// </para>
/// </remarks>
public sealed partial class JournalViewModel : ObservableObject
{
    private readonly IActionJournalReader _journal;
    private readonly ILocalizationService _text;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;


    [ObservableProperty]
    public partial string RefreshLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public ObservableCollection<ActionRecordViewModel> Records { get; } = [];

    public JournalViewModel(IActionJournalReader journal, ILocalizationService text)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Journal");
        Subtitle = _text.Get("Journal_Subtitle");
        RefreshLabel = _text.Get("Journal_Refresh");
        EmptyMessage = _text.Get("Journal_Empty");

        await ReloadAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        Records.Clear();

        IReadOnlyList<ActionRecordView> entries;
        try
        {
            entries = await _journal.ReadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            entries = [];
        }

        foreach (var entry in entries)
        {
            Records.Add(new ActionRecordViewModel
            {
                Category = _text.Get(entry.CategoryResourceKey),
                Status = _text.Get(entry.StatusResourceKey),
                Risk = _text.Get(entry.RiskResourceKey),
                When = entry.TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                NeededAdministrator = entry.NeededAdministrator,
                AdministratorLabel = _text.Get("Journal_Administrator"),
                Affected = Affected(entry.AffectedItemCount),
                HasAffected = entry.AffectedItemCount is not null,
            });
        }

        IsEmpty = Records.Count == 0;
    }

    /// <summary>
    /// The count in words, with nothing said when there is no count to give.
    /// </summary>
    /// <remarks>
    /// Zero gets its own sentence rather than "0 объектов". An entry that touched nothing is the
    /// one this exists to make visible, and reading it as a number beside three others invites the
    /// eye straight past it.
    /// </remarks>
    private string Affected(int? count) => count switch
    {
        null => string.Empty,
        0 => _text.Get("Journal_AffectedNone"),
        _ => string.Format(CultureInfo.CurrentCulture, _text.Get("Journal_Affected"), count),
    };
}
