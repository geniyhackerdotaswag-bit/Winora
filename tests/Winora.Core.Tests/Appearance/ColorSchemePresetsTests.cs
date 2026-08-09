using Winora.Core.Appearance;
using Xunit;

namespace Winora.Core.Tests.Appearance;

/// <summary>
/// The schemes Winora ships. Each one is held to the floor it refuses to let a user cross.
/// </summary>
/// <remarks>
/// A preset that fails its own gate would be worse than any scheme a person could assemble by hand:
/// it arrives pre-selected, it carries the app's endorsement, and the editor would then refuse to
/// re-apply the very thing it shipped with.
/// </remarks>
public sealed class ColorSchemePresetsTests
{
    public static TheoryData<string> PresetIds()
    {
        var data = new TheoryData<string>();
        foreach (var preset in ColorSchemePresets.All)
        {
            data.Add(preset.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void Every_preset_clears_the_text_floor(string id)
    {
        var preset = ColorSchemePresets.Require(id);
        var report = SchemeContrast.Measure(SchemeDerivation.Derive(preset.Scheme));

        Assert.True(
            report.TextPasses,
            $"Preset '{id}' ships text below the floor:\n  " + string.Join(
                "\n  ",
                report.Checks
                    .Where(static check => !check.Passes && check.Floor == SchemeContrast.TextFloor)
                    .Select(static check =>
                        $"{check.Id}: {check.Foreground.ToHex()} on {check.Surface.ToHex()} — {check.Ratio:F2}:1")));
    }

    /// <summary>
    /// Clearing the floor by a hundredth is a coincidence, not a margin.
    /// </summary>
    /// <remarks>
    /// The light theme's faint tone shipped at 4.51:1 during development, and would have stayed
    /// there: the plain pass/fail assertion above was green. Anything that rounds differently — a
    /// changed step, a different rounding mode — moves it under. This holds every shipped tone far
    /// enough from the boundary that it takes a real regression to cross it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(PresetIds))]
    public void Every_preset_clears_the_text_floor_with_room_to_spare(string id)
    {
        const double RequiredHeadroom = 0.25;

        var report = SchemeContrast.Measure(
            SchemeDerivation.Derive(ColorSchemePresets.Require(id).Scheme));

        var tight = report.Checks
            .Where(static check => check.Role is ContrastRole.Text)
            .Where(check => check.Ratio < SchemeContrast.TextFloor + RequiredHeadroom)
            .ToArray();

        Assert.True(
            tight.Length == 0,
            $"Preset '{id}' sits on the boundary:\n  " + string.Join(
                "\n  ",
                tight.Select(static check => $"{check.Id}: {check.Ratio:F2}:1")));
    }

    [Fact]
    public void Preset_identifiers_are_unique()
    {
        var ids = ColorSchemePresets.All.Select(static preset => preset.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Identifiers are persisted in <c>app-settings.json</c> and are never derived from a display
    /// name, for the same reason route keys are not: a rename in one language would silently discard
    /// the user's choice on the next launch.
    /// </summary>
    [Theory]
    [MemberData(nameof(PresetIds))]
    public void A_preset_identifier_is_a_stable_lowercase_slug(string id) =>
        Assert.Matches("^[a-z]+(-[a-z]+)*$", id);

    /// <summary>
    /// Every accent family ships in both a dark and a light form. Offering only one means a user on
    /// the other kind of desktop has to build their own before the app is usable, which is the
    /// opposite of what a preset is for.
    /// </summary>
    [Fact]
    public void Every_accent_family_has_a_dark_and_a_light_variant()
    {
        var byFamily = ColorSchemePresets.All.GroupBy(
            static preset => preset.Family,
            StringComparer.Ordinal);

        foreach (var family in byFamily)
        {
            var derived = family
                .Select(static preset => SchemeDerivation.Derive(preset.Scheme).IsDark)
                .ToArray();

            Assert.Contains(true, derived);
            Assert.Contains(false, derived);
        }
    }

    [Fact]
    public void The_default_preset_is_one_of_the_presets() =>
        Assert.Contains(
            ColorSchemePresets.All,
            static preset => string.Equals(preset.Id, ColorSchemePresets.DefaultId, StringComparison.Ordinal));

    [Fact]
    public void An_unknown_identifier_is_refused_rather_than_defaulted()
    {
        Assert.False(ColorSchemePresets.TryGet("no-such-preset", out _));
        Assert.Throws<KeyNotFoundException>(() => ColorSchemePresets.Require("no-such-preset"));
    }
}
