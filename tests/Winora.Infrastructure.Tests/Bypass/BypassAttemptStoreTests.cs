using Winora.Core.Bypass;
using Winora.Infrastructure.Bypass;
using Xunit;

namespace Winora.Infrastructure.Tests.Bypass;

/// <summary>
/// The file that remembers which strategies were tried.
/// </summary>
/// <remarks>
/// Every failure here reads as "no history", which puts the screen in the state a fresh install is
/// already in. Nothing about this file may stop the bypass screen from opening: it is a convenience
/// for a search, not a record the machine's safety rests on.
/// </remarks>
public sealed class BypassAttemptStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "winora-attempts-" + Guid.NewGuid().ToString("N"));

    private BypassAttemptStore Store() => new(_directory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // A temporary directory that outlives the run is not worth failing a test over.
        }
    }

    [Fact]
    public void With_no_file_there_is_no_history()
    {
        Assert.Empty(Store().Read());
    }

    [Fact]
    public void What_was_written_is_read_back()
    {
        var when = new DateTimeOffset(2026, 8, 26, 9, 30, 0, TimeSpan.Zero);
        BypassAttempt[] attempts =
        [
            new("general (ALT3)", when, BypassOutcome.Worked),
            new("general", when.AddHours(-1), BypassOutcome.Failed),
        ];

        Assert.True(Store().Write(attempts));

        var read = Store().Read();

        Assert.Equal(2, read.Count);
        Assert.Equal("general (ALT3)", read[0].StrategyId);
        Assert.Equal(BypassOutcome.Worked, read[0].Outcome);
        Assert.Equal(when, read[0].WhenUtc);
    }

    /// <summary>
    /// The names are the release's own file names, brackets and all. A store that mangled them
    /// would break the one thing they are for: matching what a forum thread tells somebody to try.
    /// </summary>
    [Theory]
    [InlineData("general")]
    [InlineData("general (ALT12)")]
    [InlineData("discord (ALT)")]
    public void A_strategy_name_survives_the_round_trip_exactly(string id)
    {
        Assert.True(Store().Write([new(id, DateTimeOffset.UtcNow, BypassOutcome.Failed)]));

        Assert.Equal(id, Store().Read()[0].StrategyId);
    }

    [Fact]
    public void Newest_comes_first_however_it_was_written()
    {
        var when = new DateTimeOffset(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
        BypassAttempt[] attempts =
        [
            new("general", when.AddHours(-5), BypassOutcome.Failed),
            new("general (ALT)", when, BypassOutcome.Worked),
        ];

        Assert.True(Store().Write(attempts));

        Assert.Equal("general (ALT)", Store().Read()[0].StrategyId);
    }

    [Fact]
    public void A_file_that_is_not_json_reads_as_no_history()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "bypass-attempts.json"), "not json at all");

        Assert.Empty(Store().Read());
    }

    /// <summary>A row naming no strategy can never match one, so it is not kept as a blank line.</summary>
    [Fact]
    public void A_row_with_no_strategy_is_dropped_and_the_rest_survive()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "bypass-attempts.json"),
            """
            [
              { "strategyId": "", "whenUtc": "2026-08-26T09:00:00+00:00", "outcome": 2 },
              { "strategyId": "general", "whenUtc": "2026-08-26T08:00:00+00:00", "outcome": 1 }
            ]
            """);

        var read = Store().Read();

        Assert.Single(read);
        Assert.Equal("general", read[0].StrategyId);
    }

    /// <summary>
    /// A verdict this build does not know reads as "nobody has judged this", which keeps the
    /// strategy in the search rather than ruling it in or out on a value we cannot interpret.
    /// </summary>
    [Fact]
    public void An_outcome_from_a_newer_build_reads_as_unjudged()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "bypass-attempts.json"),
            """
            [{ "strategyId": "general", "whenUtc": "2026-08-26T09:00:00+00:00", "outcome": 77 }]
            """);

        Assert.Equal(BypassOutcome.Unknown, Store().Read()[0].Outcome);
    }

    [Fact]
    public void More_than_the_cap_is_trimmed_on_the_way_in()
    {
        Directory.CreateDirectory(_directory);
        var rows = Enumerable
            .Range(0, BypassAttemptRules.MaxKept + 40)
            .Select(index =>
                $$"""{ "strategyId": "general", "whenUtc": "2026-08-26T09:00:{{index % 60:00}}+00:00", "outcome": 0 }""");

        File.WriteAllText(
            Path.Combine(_directory, "bypass-attempts.json"),
            "[" + string.Join(",", rows) + "]");

        Assert.Equal(BypassAttemptRules.MaxKept, Store().Read().Count);
    }

    [Fact]
    public void Writing_over_an_existing_record_replaces_it()
    {
        var when = DateTimeOffset.UtcNow;

        Assert.True(Store().Write([new("general", when, BypassOutcome.Failed)]));
        Assert.True(Store().Write([new("general (ALT)", when, BypassOutcome.Worked)]));

        var read = Store().Read();

        Assert.Single(read);
        Assert.Equal("general (ALT)", read[0].StrategyId);
    }

    /// <summary>The temporary file used for the move is never left behind.</summary>
    [Fact]
    public void Writing_leaves_nothing_beside_the_record()
    {
        Assert.True(Store().Write([new("general", DateTimeOffset.UtcNow, BypassOutcome.Worked)]));

        Assert.Equal(
            ["bypass-attempts.json"],
            Directory.GetFiles(_directory).Select(Path.GetFileName).Order());
    }
}
