namespace Winora.Core.Appearance;

/// <summary>Why the scheme in use is the one it is.</summary>
public enum ColorSchemeLoadOutcome
{
    /// <summary>Read back exactly as it was saved.</summary>
    Stored,

    /// <summary>Nothing saved yet — a first run, or a store that has been reset.</summary>
    Missing,

    /// <summary>The file exists but could not be read or does not hold colours.</summary>
    Unreadable,

    /// <summary>
    /// The file parsed but the scheme it describes puts text below the readable floor, so it was
    /// refused.
    /// </summary>
    /// <remarks>
    /// The editor never saves such a scheme, so reaching this normally means the floors themselves
    /// were tightened after the scheme was stored. It is checked anyway, because an app whose text
    /// cannot be read cannot be used to repair itself.
    /// </remarks>
    Rejected,
}

/// <param name="Scheme">The scheme to use — the stored one, or the default when it was not usable.</param>
public sealed record ColorSchemeLoad(WinoraColorScheme Scheme, ColorSchemeLoadOutcome Outcome);

/// <summary>
/// Where the user's chosen colours live between sessions.
/// </summary>
/// <remarks>
/// Loading must never fail. A scheme that cannot be read falls back to the default and says so
/// through <see cref="ColorSchemeLoadOutcome" />, because the alternative is an app that will not
/// start over a cosmetic preference — and because the one route to a bad file is a person editing
/// it, which is a thing they are allowed to do.
/// </remarks>
public interface IColorSchemeStore
{
    ValueTask<ColorSchemeLoad> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(WinoraColorScheme scheme, CancellationToken cancellationToken = default);
}
