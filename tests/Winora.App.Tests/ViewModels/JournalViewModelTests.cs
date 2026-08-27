using Winora.App.Services;
using Winora.App.ViewModels;
using Xunit;

namespace Winora.App.Tests.ViewModels;

/// <summary>
/// The audit trail, and the one thing it could not say.
/// </summary>
/// <remarks>
/// This screen deliberately holds no paths, values or names, so that somebody can share it when
/// something has gone wrong. What it also held was no counts — and four cleanup entries written
/// minutes apart therefore read identically, including the one that had removed nothing at all. A
/// log where a change and a no-op look the same is a log nobody can use.
/// </remarks>
public sealed class JournalViewModelTests
{
    private sealed class Text : ILocalizationService
    {
        public bool IsAvailable => true;

        public string Get(string key) => key switch
        {
            "Journal_Affected" => "затронуто объектов: {0}",
            "Journal_AffectedNone" => "ничего не затронуто",
            _ => key,
        };
    }

    private sealed class Reader : IActionJournalReader
    {
        public required IReadOnlyList<ActionRecordView> Records { get; init; }

        public Task<IReadOnlyList<ActionRecordView>> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Records);
    }

    private static ActionRecordView Record(int? affected) => new(
        DateTimeOffset.UnixEpoch,
        "Journal_Category_Cleanup",
        "Journal_Status_Completed",
        "Journal_Risk_Low",
        NeededAdministrator: false,
        AffectedItemCount: affected);

    private static async Task<JournalViewModel> Loaded(params int?[] counts)
    {
        var vm = new JournalViewModel(
            new Reader { Records = [.. counts.Select(Record)] },
            new Text());

        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task How_many_things_were_touched_is_shown()
    {
        var vm = await Loaded(264);

        Assert.Equal("затронуто объектов: 264", vm.Records[0].Affected);
        Assert.True(vm.Records[0].HasAffected);
    }

    /// <summary>
    /// An entry that touched nothing says so in words.
    /// </summary>
    /// <remarks>
    /// This is the entry the whole change exists for. "0 объектов" beside three real numbers is
    /// something the eye goes straight past; a sentence is not.
    /// </remarks>
    [Fact]
    public async Task An_entry_that_touched_nothing_says_so_rather_than_showing_a_zero()
    {
        var vm = await Loaded(0);

        Assert.Equal("ничего не затронуто", vm.Records[0].Affected);
        Assert.True(vm.Records[0].HasAffected);
    }

    /// <summary>An operation that counts in nothing says nothing, rather than "0".</summary>
    [Fact]
    public async Task An_operation_with_nothing_to_count_stays_silent()
    {
        var vm = await Loaded((int?)null);

        Assert.Equal(string.Empty, vm.Records[0].Affected);
        Assert.False(vm.Records[0].HasAffected);
    }

    /// <summary>
    /// Entries that differ only in their counts no longer read the same.
    /// </summary>
    /// <remarks>
    /// The exact case seen on the owner's machine: four cleanup runs minutes apart, indistinguishable
    /// on screen, one of which had done nothing.
    /// </remarks>
    [Fact]
    public async Task Entries_that_differ_only_in_count_no_longer_look_alike()
    {
        var vm = await Loaded(264, 15, 2, 0);

        var lines = vm.Records.Select(static r => r.Affected).ToArray();

        Assert.Equal(4, lines.Distinct().Count());
    }

    /// <summary>Nothing that identifies a target reaches this screen, count or no count.</summary>
    [Fact]
    public async Task The_row_still_carries_nothing_that_names_a_target()
    {
        var vm = await Loaded(264);
        var row = vm.Records[0];

        foreach (var shown in new[] { row.Category, row.Status, row.Risk, row.Affected })
        {
            Assert.DoesNotContain(":\\", shown, StringComparison.Ordinal);
            Assert.DoesNotContain("winora.", shown, StringComparison.Ordinal);
        }
    }
}
