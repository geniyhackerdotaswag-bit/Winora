namespace Winora.System.Windows;

/// <summary>
/// Which visual effects Windows will not change while the master switch is off.
/// </summary>
/// <remarks>
/// <para>
/// <c>SPI_SETUIEFFECTS</c> gates most of this domain. Measured on 2026-08-02: with it off, Windows
/// accepts a write to a dependent setting, returns success, and leaves the value unchanged. An
/// operation that reported such a setting as changeable would promise a change it cannot make and
/// then "verify" a value it never wrote.
/// </para>
/// <para>
/// The list is exactly the effects Microsoft documents as governed by the UI-effects flag — the ones
/// whose <c>SystemParametersInfo</c> entries state the setting is ignored while UI effects are
/// disabled. It is deliberately not "everything else": gating an independent setting would be the
/// mirror-image lie, refusing a change that would in fact succeed. Anything not listed here is
/// treated as independent, and a new member of <see cref="VisualEffectSetting" /> is independent
/// until someone shows otherwise.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow
/// </remarks>
public static class VisualEffectDependencies
{
    /// <summary>The effects that cannot be changed while <c>UIEFFECTS</c> is off.</summary>
    public static IReadOnlyList<VisualEffectSetting> DependentOnUiEffects { get; } =
    [
        VisualEffectSetting.ComboBoxAnimation,
        VisualEffectSetting.CursorShadow,
        VisualEffectSetting.GradientCaptions,
        VisualEffectSetting.HotTracking,
        VisualEffectSetting.ListBoxSmoothScrolling,
        VisualEffectSetting.MenuAnimation,
        VisualEffectSetting.MenuFade,
        VisualEffectSetting.SelectionFade,
        VisualEffectSetting.TooltipAnimation,
        VisualEffectSetting.TooltipFade,
    ];

    /// <summary>
    /// True when <paramref name="setting" /> needs the master switch on. Never true for the master
    /// switch itself: gating it would make turning effects back on impossible.
    /// </summary>
    public static bool DependsOnUiEffects(VisualEffectSetting setting) =>
        setting != VisualEffectSetting.UiEffects && DependentOnUiEffects.Contains(setting);
}
