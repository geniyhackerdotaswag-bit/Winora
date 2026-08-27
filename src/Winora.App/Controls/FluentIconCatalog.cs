namespace Winora.App.Controls;

/// <summary>
/// The single icon catalog. Every glyph in the shell resolves through here so no screen can invent
/// its own icon or its own size.
/// </summary>
/// <remarks>
/// <para>
/// Specification section 12 calls for vendored Microsoft Fluent System Icons Regular 20 SVG assets.
/// Those assets are not yet vendored into the repository, so this catalog currently maps to the
/// Windows 11 system font "Segoe Fluent Icons". The indirection is the point: swapping to the SVG
/// assets later changes this one file and the presenter, not any page. Mixing sources, per-page
/// sizing, and emoji remain forbidden either way.
/// </para>
/// <para>
/// A second kind exists because that font carries no third-party brand marks: an icon may instead
/// carry path mini-language and be drawn as a vector. Only the Discord mark needs it, and it is not
/// a licence to hand-draw icons the font already has.
/// </para>
/// </remarks>
public static class FluentIconCatalog
{
    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.Ordinal)
    {
        ["home"] = "",
        ["color"] = "",
        ["appearance"] = "",
        ["taskbar"] = "",
        ["sound"] = "",
        ["cursor"] = "",
        ["explorer"] = "",
        ["speed"] = "",
        ["broom"] = "",
        ["startup"] = "",
        ["globe"] = "",
        ["history"] = "",
        ["backup"] = "",
        ["journal"] = "",
        ["settings"] = "",
        ["profile"] = "",
    };

    /// <summary>
    /// Icons drawn as a vector path, held as path mini-language rather than as a built
    /// <c>Geometry</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately the raw text, not a <c>Geometry</c> in a <c>ResourceDictionary</c>. That was
    /// tried first, on the assumption that a Geometry is not a UIElement and so may be shared by
    /// several icons. It cannot: WinUI's <c>PathIcon.Data</c> rejects a geometry owned by a
    /// resource dictionary with <c>E_INVALIDARG</c>, and the app failed to open at all. Each
    /// consumer parses its own instance from this text instead.
    /// </remarks>
    /// <remarks>
    /// One literal per icon, however long the line. Split across concatenated string pieces, a
    /// dropped or reordered fragment is invisible in review and produces a subtly wrong shape.
    /// </remarks>
    private static readonly Dictionary<string, string> PathData = new(StringComparer.Ordinal)
    {
        ["discord"] = "M20.317 4.369a19.79 19.79 0 0 0-4.885-1.515a.074.074 0 0 0-.079.037c-.211.375-.445.865-.608 1.25a18.27 18.27 0 0 0-5.487 0a12.6 12.6 0 0 0-.617-1.25a.077.077 0 0 0-.079-.037A19.736 19.736 0 0 0 3.677 4.37a.07.07 0 0 0-.032.027C.533 9.046-.32 13.58.099 18.057a.082.082 0 0 0 .031.057a19.9 19.9 0 0 0 5.993 3.03a.078.078 0 0 0 .084-.028c.462-.63.874-1.295 1.226-1.994a.076.076 0 0 0-.041-.106a13.1 13.1 0 0 1-1.872-.892a.077.077 0 0 1-.008-.128a10.2 10.2 0 0 0 .372-.292a.074.074 0 0 1 .077-.01c3.928 1.793 8.18 1.793 12.062 0a.074.074 0 0 1 .078.009c.12.099.246.198.373.293a.077.077 0 0 1-.006.127a12.3 12.3 0 0 1-1.873.891a.077.077 0 0 0-.041.107c.36.698.772 1.362 1.225 1.993a.076.076 0 0 0 .084.028a19.84 19.84 0 0 0 6.002-3.03a.077.077 0 0 0 .032-.054c.5-5.177-.838-9.674-3.549-13.66a.061.061 0 0 0-.031-.03zM8.02 15.331c-1.183 0-2.157-1.086-2.157-2.419c0-1.333.955-2.419 2.157-2.419c1.211 0 2.176 1.096 2.157 2.42c0 1.332-.956 2.418-2.157 2.418zm7.975 0c-1.183 0-2.157-1.086-2.157-2.419c0-1.333.955-2.419 2.157-2.419c1.211 0 2.176 1.096 2.157 2.42c0 1.332-.946 2.418-2.157 2.418z",
    };

    public static bool TryGetGlyph(string key, out string glyph) => Glyphs.TryGetValue(key, out glyph!);

    public static bool TryGetPathData(string key, out string pathData) =>
        PathData.TryGetValue(key, out pathData!);

    public static string GetGlyph(string key) =>
        TryGetGlyph(key, out var glyph)
            ? glyph
            : throw new KeyNotFoundException($"Icon '{key}' is not in the Winora icon catalog.");

    /// <summary>Every key the catalog can resolve, of either kind.</summary>
    public static IReadOnlyCollection<string> Keys => [.. Glyphs.Keys, .. PathData.Keys];
}
