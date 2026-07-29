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
    [Theory]
    [InlineData(VisualEffectSetting.ClientAreaAnimation)]
    [InlineData(VisualEffectSetting.UiEffects)]
    public void Both_documented_actions_are_present_and_readable_on_windows_11(VisualEffectSetting setting)
    {
        var reading = new WindowsVisualEffectsAccess().Read(setting);

        Assert.True(reading.IsActionAvailable, $"The SPI action for {setting} was reported as absent.");
        Assert.True(reading.IsReadable, $"The current value for {setting} could not be read.");
    }

    [Theory]
    [InlineData(VisualEffectSetting.ClientAreaAnimation)]
    [InlineData(VisualEffectSetting.UiEffects)]
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
}
