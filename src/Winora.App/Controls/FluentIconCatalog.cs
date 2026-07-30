namespace Winora.App.Controls;

/// <summary>
/// The single icon catalog. Every glyph in the shell resolves through here so no screen can invent
/// its own icon or its own size.
/// </summary>
/// <remarks>
/// Specification section 12 calls for vendored Microsoft Fluent System Icons Regular 20 SVG assets.
/// Those assets are not yet vendored into the repository, so this catalog currently maps to the
/// Windows 11 system font "Segoe Fluent Icons". The indirection is the point: swapping to the SVG
/// assets later changes this one file and the presenter, not any page. Mixing sources, per-page
/// sizing, and emoji remain forbidden either way.
/// </remarks>
public static class FluentIconCatalog
{
    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.Ordinal)
    {
        ["home"] = "",
        ["color"] = "",
        ["taskbar"] = "",
        ["sound"] = "",
        ["cursor"] = "",
        ["icon"] = "",
        ["speed"] = "",
        ["broom"] = "",
        ["startup"] = "",
        ["history"] = "",
        ["backup"] = "",
        ["journal"] = "",
        ["settings"] = "",
    };

    public static bool TryGetGlyph(string key, out string glyph) => Glyphs.TryGetValue(key, out glyph!);

    public static string GetGlyph(string key) =>
        TryGetGlyph(key, out var glyph)
            ? glyph
            : throw new KeyNotFoundException($"Icon '{key}' is not in the Winora icon catalog.");

    public static IReadOnlyCollection<string> Keys => Glyphs.Keys;
}
