using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// Read-only smoke coverage for the real <c>SystemParametersInfoW</c> adapter. These tests prove
/// the documented action identifiers and marshalling are correct against the running session.
/// They never write, so they cannot change the developer's own Windows configuration.
/// </summary>
public sealed class WindowsVisualEffectsAccessTests
{
    public static TheoryData<VisualEffectSetting> AllSettings()
    {
        var data = new TheoryData<VisualEffectSetting>();
        foreach (var setting in Enum.GetValues<VisualEffectSetting>())
        {
            data.Add(setting);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllSettings))]
    public void Every_documented_action_is_present_and_readable_on_windows_11(VisualEffectSetting setting)
    {
        var reading = new WindowsVisualEffectsAccess().Read(setting);

        Assert.True(reading.IsActionAvailable, $"The SPI action for {setting} was reported as absent.");
        Assert.True(reading.IsReadable, $"The current value for {setting} could not be read.");
    }

    [Theory]
    [MemberData(nameof(AllSettings))]
    public void Reading_is_stable_and_free_of_side_effects(VisualEffectSetting setting)
    {
        var access = new WindowsVisualEffectsAccess();

        Assert.Equal(access.Read(setting), access.Read(setting));
    }

    [Fact]
    public void An_undefined_setting_is_rejected_rather_than_mapped_to_an_arbitrary_action()
    {
        var access = new WindowsVisualEffectsAccess();

        Assert.Throws<ArgumentOutOfRangeException>(() => access.Read((VisualEffectSetting)999));
    }

    [Fact]
    public void Every_setting_has_a_descriptor()
    {
        foreach (var setting in Enum.GetValues<VisualEffectSetting>())
        {
            Assert.True(
                VisualEffectActions.TryGet(setting, out _),
                $"{setting} has no action descriptor.");
        }
    }

    [Fact]
    public void Action_identifiers_are_unique_so_no_two_settings_address_the_same_target()
    {
        var descriptors = Enum.GetValues<VisualEffectSetting>()
            .Select(VisualEffectActions.For)
            .ToArray();

        var gets = descriptors.Select(static d => d.GetAction).ToArray();
        var sets = descriptors.Select(static d => d.SetAction).ToArray();

        Assert.Equal(gets.Length, gets.Distinct().Count());
        Assert.Equal(sets.Length, sets.Distinct().Count());
        Assert.Empty(gets.Intersect(sets));
    }

    /// <summary>
    /// Two of the documented actions predate the 0x10xx range and pass the value in
    /// <c>uiParam</c> with a null <c>pvParam</c>. Writing them the usual way would silently set the
    /// wrong thing, so the distinction is asserted rather than left to a comment.
    /// </summary>
    [Fact]
    public void The_two_legacy_actions_are_the_only_ones_that_carry_the_value_in_uiParam()
    {
        var legacy = Enum.GetValues<VisualEffectSetting>()
            .Where(static setting => VisualEffectActions.For(setting).WriteStyle == VisualEffectWriteStyle.UiParam)
            .OrderBy(static setting => setting.ToString(), StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { VisualEffectSetting.DragFullWindows, VisualEffectSetting.FontSmoothing },
            legacy);
    }

    [Fact]
    public void An_undefined_setting_has_no_descriptor()
    {
        Assert.False(VisualEffectActions.TryGet((VisualEffectSetting)999, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => VisualEffectActions.For((VisualEffectSetting)999));
    }
}
