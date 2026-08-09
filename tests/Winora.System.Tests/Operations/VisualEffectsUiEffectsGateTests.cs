using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.System.Operations;
using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Operations;

/// <summary>
/// <c>UIEFFECTS</c> is the master switch for most of this domain. While it is off, Windows accepts a
/// write to a dependent setting, reports success, and leaves the value unchanged — measured on
/// 2026-08-02. Reporting such a setting as changeable would make the app claim a change it cannot
/// make, which is the exact failure this project exists to prevent.
/// </summary>
public sealed class VisualEffectsUiEffectsGateTests
{
    [Fact]
    public void A_dependent_setting_is_not_writable_while_ui_effects_are_off()
    {
        var access = new PerSettingAccess();
        access.Set(VisualEffectSetting.UiEffects, false);
        access.Set(VisualEffectSetting.MenuAnimation, false);

        var capability = Probe(VisualEffectSetting.MenuAnimation, access);

        Assert.False(capability.IsWritable);
        Assert.NotEqual(SupportStatus.Supported, capability.Support);
        Assert.False(string.IsNullOrWhiteSpace(capability.BlockReason));
    }

    [Fact]
    public void A_dependent_setting_is_writable_once_ui_effects_are_on()
    {
        var access = new PerSettingAccess();
        access.Set(VisualEffectSetting.UiEffects, true);
        access.Set(VisualEffectSetting.MenuAnimation, false);

        Assert.True(Probe(VisualEffectSetting.MenuAnimation, access).IsWritable);
    }

    /// <summary>
    /// The master switch must never gate itself, or turning effects back on would be impossible.
    /// </summary>
    [Fact]
    public void The_master_switch_stays_writable_while_it_is_off()
    {
        var access = new PerSettingAccess();
        access.Set(VisualEffectSetting.UiEffects, false);

        var capability = Probe(VisualEffectSetting.UiEffects, access);

        Assert.True(capability.IsWritable);
        Assert.Equal(SupportStatus.Supported, capability.Support);
    }

    /// <summary>
    /// Settings Windows documents as independent keep working with effects off. Gating everything
    /// would be the mirror-image lie: refusing a change that would in fact succeed.
    /// </summary>
    [Theory]
    [InlineData(VisualEffectSetting.FontSmoothing)]
    [InlineData(VisualEffectSetting.DragFullWindows)]
    [InlineData(VisualEffectSetting.ClientAreaAnimation)]
    public void An_independent_setting_stays_writable_while_ui_effects_are_off(VisualEffectSetting setting)
    {
        var access = new PerSettingAccess();
        access.Set(VisualEffectSetting.UiEffects, false);
        access.Set(setting, false);

        Assert.True(Probe(setting, access).IsWritable);
    }

    [Fact]
    public void Every_dependent_setting_is_gated()
    {
        var access = new PerSettingAccess();
        access.Set(VisualEffectSetting.UiEffects, false);

        foreach (var setting in VisualEffectDependencies.DependentOnUiEffects)
        {
            access.Set(setting, false);
            Assert.False(Probe(setting, access).IsWritable);
        }
    }

    /// <summary>The master switch cannot be in its own dependent list without deadlocking itself.</summary>
    [Fact]
    public void The_master_switch_is_not_listed_as_its_own_dependent() =>
        Assert.DoesNotContain(VisualEffectSetting.UiEffects, VisualEffectDependencies.DependentOnUiEffects);

    /// <summary>
    /// A setting whose master switch cannot be read is not assumed to be fine. An unreadable master
    /// is unknown, and unknown must not be reported as changeable.
    /// </summary>
    [Fact]
    public void A_dependent_setting_is_not_writable_when_the_master_switch_is_unreadable()
    {
        var access = new PerSettingAccess();
        access.SetUnreadable(VisualEffectSetting.UiEffects);
        access.Set(VisualEffectSetting.MenuAnimation, false);

        Assert.False(Probe(VisualEffectSetting.MenuAnimation, access).IsWritable);
    }

    private static OperationCapability Probe(VisualEffectSetting setting, IVisualEffectsAccess access) =>
        new VisualEffectsOperation(setting, access)
            .ProbeAsync(new OperationTarget(VisualEffectsOperation.IdFor(setting)), default)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Answers per setting, unlike the older fake that returned one value for every setting. That
    /// shape could not express "the master switch is off but this one is on", so it could not have
    /// caught this bug at all.
    /// </summary>
    private sealed class PerSettingAccess : IVisualEffectsAccess
    {
        private readonly Dictionary<VisualEffectSetting, bool> _values = [];
        private readonly HashSet<VisualEffectSetting> _unreadable = [];

        public void Set(VisualEffectSetting setting, bool value) => _values[setting] = value;

        public void SetUnreadable(VisualEffectSetting setting) => _unreadable.Add(setting);

        public VisualEffectReading Read(VisualEffectSetting setting) =>
            _unreadable.Contains(setting)
                ? new VisualEffectReading(true, false, false)
                : new VisualEffectReading(true, true, _values.GetValueOrDefault(setting));

        public VisualEffectWriteOutcome Write(VisualEffectSetting setting, bool value)
        {
            _values[setting] = value;
            return VisualEffectWriteOutcome.Written;
        }
    }
}
