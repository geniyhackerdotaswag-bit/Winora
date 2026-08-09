namespace Winora.Core.Appearance;

/// <summary>One scheme Winora ships ready-made.</summary>
/// <param name="Id">Stable slug, persisted. Never derived from a display name.</param>
/// <param name="Family">
/// Which accent this belongs to. Two presets share a family when they differ only in whether the
/// canvas is dark or light.
/// </param>
/// <param name="NameResourceKey">Resource key for the family's name.</param>
/// <param name="VariantResourceKey">Resource key for "dark" or "light".</param>
public sealed record ColorSchemePreset(
    string Id,
    string Family,
    string NameResourceKey,
    string VariantResourceKey,
    WinoraColorScheme Scheme);

/// <summary>
/// The ready-made schemes, and the one Winora starts with.
/// </summary>
/// <remarks>
/// <para>
/// The palette is drawn from the reference the owner chose: a near-black canvas, greys, and a single
/// accent. The accent families are white, violet, red and graphite because those were the four
/// compared side by side before this was built, and each is here on its own merits — white is the
/// reference's own model, where priority is carried by fill rather than by hue; violet is Winora's
/// previous identity; red is the loudest option; graphite is the quietest.
/// </para>
/// <para>
/// Every preset is measured by <c>ColorSchemePresetsTests</c> against the same floor the editor
/// enforces. A preset that failed its own gate would be worse than anything a user could assemble:
/// it arrives pre-selected and carries the app's endorsement.
/// </para>
/// </remarks>
public static class ColorSchemePresets
{
    private const string DarkCanvas = "#0C0C0F";
    private const string LightCanvas = "#F3F3F5";

    /// <summary>
    /// The scheme a fresh installation uses.
    /// </summary>
    /// <remarks>
    /// White on near-black: the reference's own model, and the design this redesign was pointed at.
    /// Changing it is one click on the appearance screen.
    /// </remarks>
    public const string DefaultId = "white-dark";

    public static IReadOnlyList<ColorSchemePreset> All { get; } =
    [
        Preset("white-dark", "white", "Appearance_Preset_White", dark: true, DarkCanvas, "#FFFCFC"),
        Preset("white-light", "white", "Appearance_Preset_White", dark: false, LightCanvas, "#101014"),

        Preset("violet-dark", "violet", "Appearance_Preset_Violet", dark: true, DarkCanvas, "#A78BFA"),
        Preset("violet-light", "violet", "Appearance_Preset_Violet", dark: false, LightCanvas, "#6D3FD4"),

        Preset("red-dark", "red", "Appearance_Preset_Red", dark: true, DarkCanvas, "#CE3535"),
        Preset("red-light", "red", "Appearance_Preset_Red", dark: false, LightCanvas, "#B62727"),

        Preset("graphite-dark", "graphite", "Appearance_Preset_Graphite", dark: true, DarkCanvas, "#08080A"),
        Preset("graphite-light", "graphite", "Appearance_Preset_Graphite", dark: false, LightCanvas, "#0C0C0F"),
    ];

    public static WinoraColorScheme Default => Require(DefaultId).Scheme;

    public static bool TryGet(string? id, out ColorSchemePreset? preset)
    {
        preset = id is null
            ? null
            : All.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal));

        return preset is not null;
    }

    /// <summary>
    /// Resolves a preset or throws. An unknown identifier is never quietly replaced by the default:
    /// a persisted choice that silently becomes a different one is indistinguishable, from the
    /// user's side, from the app forgetting what they picked.
    /// </summary>
    public static ColorSchemePreset Require(string id) =>
        TryGet(id, out var preset) && preset is not null
            ? preset
            : throw new KeyNotFoundException($"Colour scheme preset '{id}' is not registered.");

    private static ColorSchemePreset Preset(
        string id,
        string family,
        string nameResourceKey,
        bool dark,
        string canvas,
        string accent) =>
        new(
            id,
            family,
            nameResourceKey,
            dark ? "Appearance_Variant_Dark" : "Appearance_Variant_Light",
            new WinoraColorScheme
            {
                Canvas = ColorValue.Parse(canvas),
                Accent = ColorValue.Parse(accent),
                PresetId = id,
            });
}
