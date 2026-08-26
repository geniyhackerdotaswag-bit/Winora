namespace Winora.Core.Bypass;

/// <summary>How a strategy turned out.</summary>
/// <remarks>
/// Only a person can say. Winora starts <c>winws.exe</c> and can see that the process is alive; it
/// cannot see whether Discord opened or a video played, and no amount of probing would tell it —
/// what "worked" means is whatever the person came here to do. So the verdict is asked for, not
/// deduced, and until it is given the attempt stands at <see cref="Unknown"/>.
/// </remarks>
public enum BypassOutcome
{
    /// <summary>Started, and nobody has said yet whether it helped.</summary>
    Unknown = 0,

    /// <summary>The person said it worked.</summary>
    Worked = 1,

    /// <summary>The person said it did not.</summary>
    Failed = 2,
}

/// <summary>One run of one strategy, and what came of it.</summary>
/// <param name="StrategyId">The strategy's file name without its extension, exactly as published.</param>
/// <param name="WhenUtc">When it was started.</param>
/// <param name="Outcome">The person's verdict, or <see cref="BypassOutcome.Unknown"/>.</param>
public sealed record BypassAttempt(string StrategyId, DateTimeOffset WhenUtc, BypassOutcome Outcome);

/// <summary>
/// What the record of past attempts is good for.
/// </summary>
/// <remarks>
/// The bypass screen is not a menu one chooses from with knowledge; it is a search. Which strategy
/// works depends on the network in front of it, nobody can predict it — the upstream project's own
/// advice is to try them in order until something helps. What a program can do that a person cannot
/// is remember where the search got to. Without that, somebody coming back a third time cannot
/// recall whether they reached ALT5 or ALT7, and starts over.
/// </remarks>
public static class BypassAttemptRules
{
    /// <summary>
    /// How many attempts are worth keeping.
    /// </summary>
    /// <remarks>
    /// One per strategy is all the screen shows, and there are around a dozen strategies. The cap
    /// is well above that so a burst of retries cannot push out the record of what worked, and low
    /// enough that the file stays something a person could read.
    /// </remarks>
    public const int MaxKept = 200;

    /// <summary>The most recent attempt at one strategy, or null if it has never been tried.</summary>
    public static BypassAttempt? Latest(IEnumerable<BypassAttempt> attempts, string strategyId)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        if (string.IsNullOrWhiteSpace(strategyId))
        {
            return null;
        }

        return attempts
            .Where(attempt => string.Equals(attempt.StrategyId, strategyId, StringComparison.Ordinal))
            .OrderByDescending(attempt => attempt.WhenUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// The strategy that most recently worked, or null.
    /// </summary>
    /// <remarks>
    /// Judged on the newest attempt at each strategy rather than on any attempt ever: a strategy
    /// that worked in June and failed last week is not one to suggest, and a record that kept
    /// pointing at it would be worse than no record.
    /// </remarks>
    public static string? LastWorking(IEnumerable<BypassAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        return attempts
            .GroupBy(attempt => attempt.StrategyId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(attempt => attempt.WhenUtc).First())
            .Where(attempt => attempt.Outcome == BypassOutcome.Worked)
            .OrderByDescending(attempt => attempt.WhenUtc)
            .Select(attempt => attempt.StrategyId)
            .FirstOrDefault();
    }

    /// <summary>
    /// What to offer next: the first strategy in the published order that has not failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the upstream project's own, untouched. Winora does not rank strategies, does
    /// not know which is better and does not pretend to — it only skips what this machine has
    /// already been told does not help, which is the one thing the person here has established.
    /// </para>
    /// <para>
    /// A strategy that worked comes first regardless of position: if the record says this network
    /// answers to ALT3, starting anywhere else is wasting the person's evening.
    /// </para>
    /// </remarks>
    public static string? NextToTry(IReadOnlyList<string> publishedOrder, IEnumerable<BypassAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(publishedOrder);
        ArgumentNullException.ThrowIfNull(attempts);

        var kept = attempts.ToArray();

        if (LastWorking(kept) is { } worked && publishedOrder.Contains(worked, StringComparer.Ordinal))
        {
            return worked;
        }

        foreach (var id in publishedOrder)
        {
            if (Latest(kept, id) is not { Outcome: BypassOutcome.Failed })
            {
                return id;
            }
        }

        // Every one of them has been tried and none helped. There is nothing left to suggest, and
        // saying so is more use than pointing at the first again as though it were new.
        return null;
    }

    /// <summary>Adds an attempt, keeping the newest <see cref="MaxKept"/>.</summary>
    public static IReadOnlyList<BypassAttempt> Record(
        IEnumerable<BypassAttempt> attempts,
        BypassAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(attempt);

        return attempts
            .Append(attempt)
            .OrderByDescending(kept => kept.WhenUtc)
            .Take(MaxKept)
            .ToArray();
    }

    /// <summary>
    /// Settles the verdict on the newest attempt at a strategy.
    /// </summary>
    /// <remarks>
    /// Answering "did it work?" changes the attempt that question was asked about, rather than
    /// adding a second one. Two rows for one run would make the history read as twice the searching
    /// that actually happened.
    /// </remarks>
    public static IReadOnlyList<BypassAttempt> Settle(
        IEnumerable<BypassAttempt> attempts,
        string strategyId,
        BypassOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        var kept = attempts.ToArray();
        var latest = Latest(kept, strategyId);

        if (latest is null)
        {
            return kept;
        }

        return kept
            .Select(attempt => ReferenceEquals(attempt, latest)
                ? attempt with { Outcome = outcome }
                : attempt)
            .ToArray();
    }
}
