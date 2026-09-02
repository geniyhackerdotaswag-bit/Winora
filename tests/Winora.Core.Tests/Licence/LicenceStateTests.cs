using Winora.Core.Licence;
using Xunit;

namespace Winora.Core.Tests.Licence;

/// <summary>
/// When a subscription counts as running, and when the site should be asked again.
/// </summary>
/// <remarks>
/// Pure arithmetic on stored dates, kept in Core so it can be tested without a network, a store or
/// a window. Everything that decides whether somebody paid runs through here.
/// </remarks>
public sealed class LicenceStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static LicenceState Ending(TimeSpan fromNow, TimeSpan? checkedAgo = null) =>
        new("month", Now + fromNow, "DESKTOP-1", Now - (checkedAgo ?? TimeSpan.Zero));

    [Fact]
    public void Without_a_key_there_is_nothing_to_run()
    {
        Assert.False(LicenceState.None.Exists);
        Assert.False(LicenceState.None.IsActive(Now));
        Assert.Equal(0, LicenceState.None.DaysLeft(Now));
    }

    [Fact]
    public void A_subscription_runs_until_its_date()
    {
        Assert.True(Ending(TimeSpan.FromDays(30)).IsActive(Now));
        Assert.True(Ending(TimeSpan.FromSeconds(1)).IsActive(Now));
    }

    /// <summary>The moment it ends it is over — not a second of grace.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void It_stops_at_its_date(int seconds)
    {
        Assert.False(Ending(TimeSpan.FromSeconds(seconds)).IsActive(Now));
    }

    [Fact]
    public void Days_left_counts_whole_days_and_never_goes_negative()
    {
        Assert.Equal(30, Ending(TimeSpan.FromDays(30)).DaysLeft(Now));
        Assert.Equal(0, Ending(TimeSpan.FromHours(23)).DaysLeft(Now));
        Assert.Equal(0, Ending(TimeSpan.FromDays(-5)).DaysLeft(Now));
    }

    [Fact]
    public void A_fresh_check_is_not_repeated()
    {
        Assert.False(Ending(TimeSpan.FromDays(30), TimeSpan.FromHours(1)).NeedsRecheck(Now));
    }

    [Fact]
    public void An_old_check_is_repeated()
    {
        Assert.True(Ending(TimeSpan.FromDays(30), TimeSpan.FromDays(3)).NeedsRecheck(Now));
        Assert.True(Ending(TimeSpan.FromDays(30), TimeSpan.FromDays(40)).NeedsRecheck(Now));
    }

    /// <summary>
    /// A check dated in the future is the clock being moved back, and it forces a recheck.
    /// </summary>
    /// <remarks>
    /// This is the ordinary way somebody stretches a subscription: move the clock back and every
    /// stored date looks fresh again. Treating it as "ask the site now" costs an honest user one
    /// request. It does not close the hole on its own — the same trick keeps <c>IsActive</c> true —
    /// and only the server's own time settles that, which is what the recheck fetches.
    /// </remarks>
    [Fact]
    public void A_check_dated_in_the_future_forces_one()
    {
        var moved = new LicenceState("month", Now.AddDays(30), "DESKTOP-1", Now.AddDays(1));

        Assert.True(moved.NeedsRecheck(Now));
    }

    [Fact]
    public void Without_a_key_there_is_nothing_to_recheck()
    {
        Assert.False(LicenceState.None.NeedsRecheck(Now));
    }
}
