namespace Winora.Core.Licence;

/// <summary>
/// What this copy of Winora knows about its subscription.
/// </summary>
/// <param name="Plan">The plan identifier the server issued: <c>week</c>, <c>month</c> and so on.</param>
/// <param name="ExpiresUtc">When it runs out. Null when no key has been entered.</param>
/// <param name="Machine">The name this machine was linked under, as the server recorded it.</param>
/// <param name="CheckedUtc">When the server last confirmed all of the above.</param>
/// <remarks>
/// <para>
/// Held as a record of what the server said, not as a verdict of its own. Everything here can be
/// edited by hand in the store file, so nothing here is proof — it is a cache, and it is treated as
/// one: <see cref="NeedsRecheck"/> decides when to go and ask again.
/// </para>
/// <para>
/// That is worth stating plainly rather than pretending otherwise. Winora ships as one file that
/// anybody can open; a licence check inside it is a lock on an honest person's door. The thing that
/// cannot be edited locally is what the server holds, which is why the plan is to keep what people
/// pay for — cursor packs, bypass lists, settings sync — behind it rather than behind this record.
/// </para>
/// </remarks>
public sealed record LicenceState(
    string Plan,
    DateTimeOffset? ExpiresUtc,
    string? Machine,
    DateTimeOffset CheckedUtc)
{
    /// <summary>The plan identifier the site uses for a subscription without an end.</summary>
    public const string PerpetualPlan = "lifetime";

    /// <summary>The plan identifier used for trial days.</summary>
    public const string TrialPlan = "trial";

    /// <summary>
    /// A subscription that does not end.
    /// </summary>
    /// <remarks>
    /// Kept as a real date far in the future rather than as a null, so every piece of arithmetic
    /// downstream keeps working unchanged — <c>IsActive</c>, <c>NeedsRecheck</c> and the store all
    /// read a date and would have needed a special case each. Only the screen asks
    /// <see cref="IsPerpetual"/>, because "осталось дней: 3652058" is not something to show anyone.
    /// </remarks>
    public static DateTimeOffset Forever { get; } = DateTimeOffset.MaxValue;

    /// <summary>True for a subscription with no end.</summary>
    public bool IsPerpetual => string.Equals(Plan, PerpetualPlan, StringComparison.Ordinal);

    /// <summary>True while the free days are running.</summary>
    public bool IsTrial => string.Equals(Plan, TrialPlan, StringComparison.Ordinal);

    /// <summary>No key has been entered on this machine.</summary>
    public static LicenceState None { get; } =
        new(string.Empty, null, null, DateTimeOffset.MinValue);

    /// <summary>
    /// How long the app trusts a stored answer before asking the server again.
    /// </summary>
    /// <remarks>
    /// Three days, not every launch. A subscription that stops working because the internet is down
    /// for an evening would be worse than one somebody stretched by three days, and Winora is a
    /// tool people reach for precisely when their machine is misbehaving.
    /// </remarks>
    public static TimeSpan RecheckAfter { get; } = TimeSpan.FromDays(3);

    /// <summary>True when a key has been entered at all, whatever its state.</summary>
    public bool Exists => ExpiresUtc is not null;

    /// <summary>True when the subscription is running at the given moment.</summary>
    public bool IsActive(DateTimeOffset now) => ExpiresUtc is { } ends && ends > now;

    /// <summary>Whole days left, or zero when it has run out or was never started.</summary>
    public int DaysLeft(DateTimeOffset now) =>
        ExpiresUtc is { } ends && ends > now
            ? (int)Math.Floor((ends - now).TotalDays)
            : 0;

    /// <summary>
    /// Whether the server should be asked again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// True as well when the stored check is in the future, which is not a paradox but the ordinary
    /// way a subscription gets stretched: the clock is moved back, and every stored date suddenly
    /// looks fresh. Treating that as "check now" costs an honest user one request and costs the
    /// other party the trick.
    /// </para>
    /// <para>
    /// It cannot do more than that on its own. Moving the clock back far enough also keeps
    /// <see cref="IsActive"/> true, and only the server's own time settles it — which is what the
    /// recheck fetches.
    /// </para>
    /// </remarks>
    public bool NeedsRecheck(DateTimeOffset now) =>
        Exists && (now - CheckedUtc >= RecheckAfter || CheckedUtc > now);
}
