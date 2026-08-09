namespace Winora.Core.Appearance;

/// <summary>
/// Every colour the app actually paints with, worked out from a <see cref="WinoraColorScheme" />.
/// </summary>
/// <remarks>
/// All members are <c>required</c> on purpose. A palette is built in exactly one place, and a
/// forgotten member would otherwise default to black — which on a dark canvas is invisible rather
/// than obviously broken, and would ship.
/// </remarks>
public sealed record DerivedPalette
{
    /// <summary>Whether the canvas is dark enough to need light text.</summary>
    public required bool IsDark { get; init; }

    public required ColorValue Canvas { get; init; }

    /// <summary>The raised surface the page content sits on, one step off the canvas.</summary>
    public required ColorValue Sheet { get; init; }

    public required ColorValue SheetStroke { get; init; }

    public required ColorValue Card { get; init; }

    /// <summary>
    /// The worst surface in the theme for contrast, and the one hand-checking misses.
    /// </summary>
    public required ColorValue CardHover { get; init; }

    /// <summary>The hairline between rows on a grouped surface.</summary>
    public required ColorValue Divider { get; init; }

    /// <summary>The visible edge of a control: an outline button, a switch that is off.</summary>
    public required ColorValue Stroke { get; init; }

    /// <summary>The wash under a navigation row the pointer is over.</summary>
    public required ColorValue Hover { get; init; }

    public required ColorValue TextPrimary { get; init; }

    /// <summary>Navigation labels and other text that is present but not the subject.</summary>
    public required ColorValue TextSecondary { get; init; }

    public required ColorValue TextMuted { get; init; }

    public required ColorValue TextFaint { get; init; }

    public required ColorValue Accent { get; init; }

    public required ColorValue OnAccent { get; init; }

    /// <summary>The accent under the pointer.</summary>
    /// <remarks>
    /// Both this and <see cref="AccentPressed" /> follow WinUI's own model — the accent let down
    /// onto the surface behind it rather than lightened or darkened outright. That model works for
    /// any accent in either theme, where "hover is lighter" only works in a dark one.
    /// </remarks>
    public required ColorValue AccentHover { get; init; }

    /// <summary>The accent while it is being pressed.</summary>
    public required ColorValue AccentPressed { get; init; }

    /// <summary>The tinted pill behind the selected navigation item.</summary>
    public required ColorValue AccentSoft { get; init; }

    /// <summary>
    /// An outline for the primary button, present only when the accent is too close to the sheet to
    /// read as a filled control on its own. Null means the fill speaks for itself.
    /// </summary>
    public required ColorValue? AccentEdge { get; init; }
}
