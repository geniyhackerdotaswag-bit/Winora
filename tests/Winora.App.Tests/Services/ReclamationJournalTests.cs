using Winora.App.Services;
using Winora.Core.Contracts;
using Winora.Core.Journal;
using Winora.Infrastructure.Journal;
using Winora.Infrastructure.Paths;
using Winora.System.Windows;
using Xunit;

namespace Winora.App.Tests.Services;

/// <summary>
/// Temporary-file reclamation deletes the user's bytes and is deliberately not a
/// <c>ChangeCoordinator</c> operation, so the action journal is the only record it will ever leave.
/// </summary>
/// <remarks>
/// <para>
/// For several releases it left none at all. The journal admits only allowlisted catalog operation
/// identifiers, that allowlist was built purely from the registered <c>IOperation</c> instances, and
/// reclamation is not one — so nothing wrote an entry and nothing complained. Deleting files with no
/// trace anywhere was the weakest point in the whole safety story.
/// </para>
/// <para>
/// These tests run against the real <see cref="ActionJournal" /> over a temporary root rather than a
/// stub. A stub would happily accept an identifier the real allowlist rejects, which is exactly the
/// failure being guarded against.
/// </para>
/// </remarks>
public sealed class ReclamationJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "winora-reclaim-journal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temporary directory that outlives one test run is not a test failure.
        }
    }

    /// <summary>
    /// The load-bearing one: the allowlist the composition root installs must admit what the writer
    /// emits, for every location the probe can name — not merely those present on this machine.
    /// </summary>
    [Fact]
    public void Every_reclaimable_location_is_allowlisted_for_the_journal()
    {
        var catalog = BuildAllowlist();

        Assert.NotEmpty(WindowsTempLocationProbe.AllLocationIds);
        foreach (var locationId in WindowsTempLocationProbe.AllLocationIds)
        {
            Assert.True(
                catalog.IsAllowlisted(ActionJournalWriter.ReclamationOperationId(locationId)),
                $"Reclaiming '{locationId}' would delete files without a journal entry.");
        }
    }

    /// <summary>
    /// Spelled out rather than derived. The test above shares <c>AllLocationIds</c> with the code it
    /// checks, so on its own it would still pass if the whole reclamation union were dropped and
    /// that list went empty. These literals are the independent anchor.
    /// </summary>
    [Theory]
    [InlineData("winora.cleanup.user-temp")]
    [InlineData("winora.cleanup.crash-dumps")]
    [InlineData("winora.cleanup.software-distribution")]
    [InlineData("winora.cleanup.cbs-logs")]
    public void The_known_reclamation_identifiers_are_present_by_name(string catalogOperationId)
    {
        Assert.True(BuildAllowlist().IsAllowlisted(catalogOperationId));
    }

    /// <summary>The union must add to the registered operations, never replace them.</summary>
    [Fact]
    public void The_registered_operations_keep_their_place_in_the_allowlist()
    {
        var catalog = new FixedActionJournalOperationCatalog(
            JournalAllowlist.CatalogOperationIds(["winora.visual-effects.animation"]));

        Assert.True(catalog.IsAllowlisted("winora.visual-effects.animation"));
        Assert.True(catalog.IsAllowlisted("winora.cleanup.user-temp"));
        Assert.False(catalog.IsAllowlisted("winora.cleanup.invented"));
    }

    [Fact]
    public async Task A_completed_reclamation_is_recorded_as_a_retention_decision()
    {
        var journal = BuildJournal();

        await new ActionJournalWriter(journal).RecordReclamationAsync(
            "user-temp",
            @"C:\Users\example\AppData\Local\Temp",
            requiredElevation: false,
            succeeded: true,
            deletedCount: 41);

        var entry = Assert.Single(await journal.ReadAllAsync(CancellationToken.None));
        Assert.Equal("winora.cleanup.user-temp", entry.CatalogOperationId);
        Assert.Equal(ActionJournalEventKind.RetentionDecision, entry.Kind);
        Assert.Equal(ActionJournalCategory.Retention, entry.Category);
        Assert.Equal(ActionJournalStatus.RetentionCompleted, entry.Status);
        Assert.Equal(41, entry.AffectedItemCount);
    }

    /// <summary>
    /// A reclamation that threw part-way is the entry someone actually comes looking for. The count
    /// stays null rather than being reported as zero, because files may have gone before it failed.
    /// </summary>
    [Fact]
    public async Task A_failed_reclamation_is_recorded_without_inventing_a_count()
    {
        var journal = BuildJournal();

        await new ActionJournalWriter(journal).RecordReclamationAsync(
            "user-temp",
            @"C:\Users\example\AppData\Local\Temp",
            requiredElevation: false,
            succeeded: false,
            deletedCount: null);

        var entry = Assert.Single(await journal.ReadAllAsync(CancellationToken.None));
        Assert.Equal(ActionJournalStatus.RetentionFailed, entry.Status);
        Assert.Null(entry.AffectedItemCount);
    }

    /// <summary>
    /// Clearing <c>SoftwareDistribution</c> costs the ability to roll a Windows update back. If that
    /// warrants a warning on screen it warrants standing out in the trail read afterwards.
    /// </summary>
    [Fact]
    public async Task A_windows_serviced_location_is_journalled_as_administrator_and_medium_risk()
    {
        var journal = BuildJournal();

        await new ActionJournalWriter(journal).RecordReclamationAsync(
            "software-distribution",
            @"C:\Windows\SoftwareDistribution",
            requiredElevation: true,
            succeeded: true,
            deletedCount: 7);

        var entry = Assert.Single(await journal.ReadAllAsync(CancellationToken.None));
        Assert.Equal(ActionJournalPrivilege.Administrator, entry.Privilege);
        Assert.Equal(ActionJournalRisk.Medium, entry.Risk);
    }

    /// <summary>
    /// The trail has to be safe to share. It correlates two entries about the same location without
    /// disclosing where on disk the user's files were.
    /// </summary>
    [Fact]
    public async Task The_path_is_correlated_by_hash_and_never_written_down()
    {
        const string Path = @"C:\Users\example\AppData\Local\Temp";
        var journal = BuildJournal();
        var writer = new ActionJournalWriter(journal);

        await writer.RecordReclamationAsync("user-temp", Path, false, true, 1);
        // Same location, different casing: Windows paths are case-insensitive, so a second hash
        // would break correlation for no reason.
        await writer.RecordReclamationAsync("user-temp", Path.ToLowerInvariant(), false, true, 2);

        var entries = await journal.ReadAllAsync(CancellationToken.None);
        Assert.Equal(2, entries.Count);
        Assert.Equal(entries[0].TargetCorrelationHash, entries[1].TargetCorrelationHash);
        Assert.NotNull(entries[0].TargetCorrelationHash);
        Assert.DoesNotContain("example", entries[0].TargetCorrelationHash!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Temp", entries[0].TargetCorrelationHash!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A journal failure never fails the reclamation: the files are already gone by then, and
    /// throwing would turn a completed action into an apparent error.
    /// </summary>
    [Fact]
    public async Task A_journal_that_refuses_the_entry_does_not_throw_at_the_caller()
    {
        // Allowlists something else entirely, so the append is rejected inside the journal.
        var journal = new ActionJournal(
            new WinoraDataPaths(_root),
            new FixedActionJournalOperationCatalog(["winora.unrelated.operation"]));

        await new ActionJournalWriter(journal).RecordReclamationAsync(
            "user-temp", @"C:\Temp", false, true, 1);

        Assert.Empty(await journal.ReadAllAsync(CancellationToken.None));
    }

    /// <summary>
    /// The production allowlist builder, the one <c>ServiceRegistration</c> calls, given a single
    /// stand-in for the registered operation set.
    /// </summary>
    private static IActionJournalOperationCatalog BuildAllowlist() =>
        new FixedActionJournalOperationCatalog(
            JournalAllowlist.CatalogOperationIds(["winora.test.operation"]));

    private ActionJournal BuildJournal() =>
        new(new WinoraDataPaths(_root), BuildAllowlist());
}
