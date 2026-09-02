namespace Winora.Core.Licence;

/// <summary>
/// How an attempt to activate or re-check a key ended.
/// </summary>
/// <remarks>
/// One value per thing the person has to do differently. That is the whole reason this is not a
/// bool: "the key is wrong", "you have used all your machines" and "the site is unreachable" send
/// somebody to three different places, and collapsing them into "не удалось" sends them nowhere.
/// </remarks>
public enum LicenceOutcome
{
    /// <summary>The key was accepted and the subscription is running.</summary>
    Activated,

    /// <summary>The stored key is still good; nothing changed.</summary>
    Confirmed,

    /// <summary>Not a key at all — refused before any request was sent.</summary>
    Malformed,

    /// <summary>No such key, or it was withdrawn. The server does not distinguish, and neither do we.</summary>
    Rejected,

    /// <summary>The key is real and its time has run out.</summary>
    Expired,

    /// <summary>Every machine slot on this key is taken.</summary>
    DeviceLimit,

    /// <summary>The site could not be reached, or answered with something unusable.</summary>
    Unreachable,

    /// <summary>The site has no address configured in this build.</summary>
    NotConfigured,
}

/// <summary>The result of one attempt, with whatever the server told us.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="State">The subscription as it now stands. <see cref="LicenceState.None"/> on failure.</param>
/// <param name="BonusDays">Days a promo code added. Zero unless one was used and accepted.</param>
/// <param name="DeviceLimit">How many machines the key allows. Meaningful only for a refusal on that ground.</param>
public sealed record LicenceResult(
    LicenceOutcome Outcome,
    LicenceState State,
    int BonusDays = 0,
    int DeviceLimit = 0)
{
    public static LicenceResult Failed(LicenceOutcome outcome, int deviceLimit = 0) =>
        new(outcome, LicenceState.None, 0, deviceLimit);

    /// <summary>True only for the two outcomes that leave the person with a working subscription.</summary>
    public bool Succeeded => Outcome is LicenceOutcome.Activated or LicenceOutcome.Confirmed;
}
