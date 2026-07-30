namespace Winora.System.Windows;

/// <summary>
/// How a documented set action carries its value. The 0x10xx actions cast the BOOL into
/// <c>pvParam</c>; two older actions take it in <c>uiParam</c> and require a null <c>pvParam</c>.
/// Using the wrong form succeeds at the call boundary while setting the wrong thing, so the
/// distinction is modelled instead of assumed.
/// </summary>
public enum VisualEffectWriteStyle
{
    /// <summary>uiParam is 0 and the BOOL is cast into pvParam.</summary>
    PvParam,

    /// <summary>The BOOL is passed in uiParam and pvParam must be null.</summary>
    UiParam,
}

/// <param name="GetAction">Documented SPI_GET* identifier.</param>
/// <param name="SetAction">Documented SPI_SET* identifier.</param>
/// <param name="WriteStyle">How the set action carries its value.</param>
public sealed record VisualEffectActionDescriptor(
    uint GetAction,
    uint SetAction,
    VisualEffectWriteStyle WriteStyle);

/// <summary>
/// The documented action table. Every identifier here was confirmed present and readable on the
/// Windows 11 development baseline; an action absent on another build degrades through
/// <c>ERROR_INVALID_PARAMETER</c> rather than being assumed available.
/// </summary>
/// <remarks>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow
/// </remarks>
public static class VisualEffectActions
{
    private static readonly Dictionary<VisualEffectSetting, VisualEffectActionDescriptor> Table = new()
    {
        [VisualEffectSetting.MenuAnimation] = new(0x1002, 0x1003, VisualEffectWriteStyle.PvParam),
        [VisualEffectSetting.ComboBoxAnimation] = new(0x1004, 0x1005, VisualEffectWriteStyle.PvParam),
        [VisualEffectSetting.ListBoxSmoothScrolling] = new(0x1006, 0x1007, VisualEffectWriteStyle.PvParam),
        [VisualEffectSetting.GradientCaptions] = new(0x1008, 0x1009, VisualEffectWriteStyle.PvParam),
        [VisualEffectSetting.HotTracking] = new(0x100E, 0x100F, VisualEffectWriteStyle.PvParam),
        [VisualEffectSetting.MenuFade] = new(0x1012, 0x1013, VisualEffectWriteStyle.PvParam),
        [VisualEffectSetting.SelectionFade] = new(0x1014, 0x1015, VisualEffectWriteStyle.PvParam),
        [VisualEffectSetting.TooltipAnimation] = new(0x1016, 0x1017, VisualEffectWriteStyle.PvParam),
        [VisualEffectSetting.TooltipFade] = new(0x1018, 0x1019, VisualEffectWriteStyle.PvParam),
        [VisualEffectSetting.CursorShadow] = new(0x101A, 0x101B, VisualEffectWriteStyle.PvParam),
        [VisualEffectSetting.FlatMenu] = new(0x1022, 0x1023, VisualEffectWriteStyle.PvParam),
        [VisualEffectSetting.DropShadow] = new(0x1024, 0x1025, VisualEffectWriteStyle.PvParam),
        [VisualEffectSetting.UiEffects] = new(0x103E, 0x103F, VisualEffectWriteStyle.PvParam),
        [VisualEffectSetting.ClientAreaAnimation] = new(0x1042, 0x1043, VisualEffectWriteStyle.PvParam),

        // Predate the 0x10xx block: the value travels in uiParam and pvParam must be null.
        [VisualEffectSetting.FontSmoothing] = new(0x004A, 0x004B, VisualEffectWriteStyle.UiParam),
        [VisualEffectSetting.DragFullWindows] = new(0x0026, 0x0025, VisualEffectWriteStyle.UiParam),
    };

    public static bool TryGet(VisualEffectSetting setting, out VisualEffectActionDescriptor descriptor) =>
        Table.TryGetValue(setting, out descriptor!);

    public static VisualEffectActionDescriptor For(VisualEffectSetting setting) =>
        TryGet(setting, out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(setting), setting, "No documented action for this setting.");
}
