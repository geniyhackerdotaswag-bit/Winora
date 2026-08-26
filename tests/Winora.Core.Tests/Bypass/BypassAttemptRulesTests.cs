using Winora.Core.Bypass;
using Xunit;

namespace Winora.Core.Tests.Bypass;

/// <summary>
/// The record of what has already been tried, which is the only thing on that screen a program
/// knows and a person does not.
/// </summary>
public sealed class BypassAttemptRulesTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The published order, exactly as the release writes the file names.</summary>
    private static readonly string[] Published =
    [
        "general",
        "general (ALT)",
        "general (ALT2)",
        "general (ALT3)",
    ];

    private static BypassAttempt At(string id, int hoursAgo, BypassOutcome outcome) =>
        new(id, Noon.AddHours(-hoursAgo), outcome);

    [Fact]
    public void A_strategy_never_started_has_no_latest_attempt()
    {
        Assert.Null(BypassAttemptRules.Latest([], "general"));
    }

    [Fact]
    public void The_latest_attempt_is_the_newest_one_at_that_strategy()
    {
        BypassAttempt[] attempts =
        [
            At("general", 10, BypassOutcome.Failed),
            At("general", 2, BypassOutcome.Worked),
            At("general (ALT)", 1, BypassOutcome.Failed),
        ];

        var latest = BypassAttemptRules.Latest(attempts, "general");

        Assert.NotNull(latest);
        Assert.Equal(BypassOutcome.Worked, latest.Outcome);
    }

    /// <summary>
    /// Judged on the newest attempt at each strategy, not on any attempt ever.
    /// </summary>
    /// <remarks>
    /// A strategy that worked in June and failed last week is not one to suggest. A record that
    /// kept pointing at it would be worse than no record: it would send somebody back to the one
    /// thing this network has already refused.
    /// </remarks>
    [Fact]
    public void A_strategy_that_worked_and_later_failed_is_not_the_last_working_one()
    {
        BypassAttempt[] attempts =
        [
            At("general", 48, BypassOutcome.Worked),
            At("general", 1, BypassOutcome.Failed),
        ];

        Assert.Null(BypassAttemptRules.LastWorking(attempts));
    }

    [Fact]
    public void The_last_working_strategy_is_the_most_recent_one_that_worked()
    {
        BypassAttempt[] attempts =
        [
            At("general", 48, BypassOutcome.Worked),
            At("general (ALT3)", 5, BypassOutcome.Worked),
            At("general (ALT)", 1, BypassOutcome.Failed),
        ];

        Assert.Equal("general (ALT3)", BypassAttemptRules.LastWorking(attempts));
    }

    [Fact]
    public void With_no_history_the_next_to_try_is_the_first_published()
    {
        Assert.Equal("general", BypassAttemptRules.NextToTry(Published, []));
    }

    /// <summary>Skips what this machine has already been told does not help, and nothing else.</summary>
    [Fact]
    public void What_has_failed_is_skipped_in_published_order()
    {
        BypassAttempt[] attempts =
        [
            At("general", 3, BypassOutcome.Failed),
            At("general (ALT)", 2, BypassOutcome.Failed),
        ];

        Assert.Equal("general (ALT2)", BypassAttemptRules.NextToTry(Published, attempts));
    }

    /// <summary>
    /// A strategy that worked comes first whatever its position: if this network answers to ALT3,
    /// starting anywhere else is wasting the evening.
    /// </summary>
    [Fact]
    public void What_worked_is_offered_before_anything_untried()
    {
        BypassAttempt[] attempts = [At("general (ALT3)", 2, BypassOutcome.Worked)];

        Assert.Equal("general (ALT3)", BypassAttemptRules.NextToTry(Published, attempts));
    }

    /// <summary>
    /// A strategy that worked but is no longer published is not offered. Releases add and remove
    /// them, and pointing at a file that is not there would be a button that cannot be pressed.
    /// </summary>
    [Fact]
    public void What_worked_but_is_gone_from_the_release_is_not_offered()
    {
        BypassAttempt[] attempts = [At("general (ALT99)", 2, BypassOutcome.Worked)];

        Assert.Equal("general", BypassAttemptRules.NextToTry(Published, attempts));
    }

    /// <summary>
    /// Everything has been tried and none of it helped. Saying so is more use than pointing at the
    /// first one again as though it were new.
    /// </summary>
    [Fact]
    public void When_everything_has_failed_there_is_nothing_to_suggest()
    {
        var attempts = Published.Select(id => At(id, 1, BypassOutcome.Failed)).ToArray();

        Assert.Null(BypassAttemptRules.NextToTry(Published, attempts));
    }

    /// <summary>An attempt nobody has judged yet is not a failure, so it stays on offer.</summary>
    [Fact]
    public void An_unjudged_attempt_does_not_take_a_strategy_out_of_the_search()
    {
        BypassAttempt[] attempts = [At("general", 1, BypassOutcome.Unknown)];

        Assert.Equal("general", BypassAttemptRules.NextToTry(Published, attempts));
    }

    [Fact]
    public void Recording_keeps_the_newest_and_drops_the_rest()
    {
        var attempts = Enumerable
            .Range(1, BypassAttemptRules.MaxKept)
            .Select(hours => At("general", hours, BypassOutcome.Failed))
            .ToArray();

        var kept = BypassAttemptRules.Record(attempts, At("general (ALT)", 0, BypassOutcome.Worked));

        Assert.Equal(BypassAttemptRules.MaxKept, kept.Count);
        Assert.Equal("general (ALT)", kept[0].StrategyId);
    }

    /// <summary>
    /// Answering "did it work?" settles the run that was asked about rather than adding a second
    /// one — two rows for one run would make the history read as twice the searching that happened.
    /// </summary>
    [Fact]
    public void Settling_changes_the_newest_attempt_instead_of_adding_one()
    {
        BypassAttempt[] attempts =
        [
            At("general", 5, BypassOutcome.Failed),
            At("general", 1, BypassOutcome.Unknown),
        ];

        var settled = BypassAttemptRules.Settle(attempts, "general", BypassOutcome.Worked);

        Assert.Equal(2, settled.Count);
        Assert.Equal(BypassOutcome.Worked, BypassAttemptRules.Latest(settled, "general")!.Outcome);
        Assert.Single(settled, a => a.Outcome == BypassOutcome.Failed);
    }

    [Fact]
    public void Settling_a_strategy_that_was_never_started_changes_nothing()
    {
        BypassAttempt[] attempts = [At("general", 1, BypassOutcome.Unknown)];

        var settled = BypassAttemptRules.Settle(attempts, "general (ALT)", BypassOutcome.Worked);

        Assert.Equal(attempts, settled);
    }
}
