namespace Winora.Core.Appearance;

/// <summary>
/// What the user chose. Two colours are required; everything else is an override of a value Winora
/// would otherwise work out for itself.
/// </summary>
/// <remarks>
/// <para>
/// The shape is the design. A person picks a background and an accent, and
/// <see cref="SchemeDerivation" /> produces the rest — text tiers, sheet, card, hovered card,
/// divider, stroke, and the colour printed on the accent. That is not a reduced feature set: every
/// derived value has an override property here, so anyone who wants to place all twelve by hand
/// can. It is a default that cannot go wrong, in front of a freedom that can.
/// </para>
/// <para>
/// Winora's own appearance never reaches <c>ChangeCoordinator</c>. It changes nothing in Windows,
/// has no previous system value to back up and nothing for a rollback to restore, so putting it
/// through plan/backup/verify/rollback would be ceremony that protects nothing. The specification
/// already places the Winora theme in the "neither backup nor restore point" category.
/// </para>
/// </remarks>
public sealed record WinoraColorScheme
{
    /// <summary>The window, the navigation pane and the title bar.</summary>
    public required ColorValue Canvas { get; init; }

    /// <summary>
    /// The filled primary button, the switch and slider when on, the selected navigation item, and
    /// the rule under a page title. Nothing else is allowed to be coloured.
    /// </summary>
    public required ColorValue Accent { get; init; }

    /// <summary>
    /// The text printed on top of the accent. Left unset, the more legible of black and white is
    /// chosen by measurement.
    /// </summary>
    public ColorValue? OnAccent { get; init; }

    public ColorValue? TextPrimary { get; init; }

    public ColorValue? TextMuted { get; init; }

    public ColorValue? TextFaint { get; init; }

    public ColorValue? Card { get; init; }

    public ColorValue? CardHover { get; init; }

    public ColorValue? Divider { get; init; }

    public ColorValue? Stroke { get; init; }

    /// <summary>
    /// The preset this scheme came from, or null once the user has edited anything.
    /// </summary>
    /// <remarks>
    /// Stored so the editor can show which preset is selected across restarts, and never derived
    /// from a display name — a preset renamed in one locale would otherwise discard the choice.
    /// </remarks>
    public string? PresetId { get; init; }

    /// <summary>Drops the preset association, for use the moment any colour is edited.</summary>
    public WinoraColorScheme AsCustom() => this with { PresetId = null };
}
