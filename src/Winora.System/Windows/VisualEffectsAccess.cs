using System.Runtime.InteropServices;

namespace Winora.System.Windows;

/// <summary>
/// The documented per-user visual-effect preferences Winora can change directly. Each member maps
/// to one <c>SystemParametersInfoW</c> get/set action pair that stores a single BOOL.
/// </summary>
public enum VisualEffectSetting
{
    /// <summary>SPI_GETCLIENTAREAANIMATION / SPI_SETCLIENTAREAANIMATION.</summary>
    ClientAreaAnimation,

    /// <summary>SPI_GETUIEFFECTS / SPI_SETUIEFFECTS.</summary>
    UiEffects,
}

/// <summary>
/// The outcome of one documented set call. <see cref="OutcomeUnknown"/> exists because a lost or
/// ambiguous result must never be reported as "nothing happened"; the coordinator treats it as a
/// state requiring recovery rather than a clean failure.
/// </summary>
public enum VisualEffectWriteOutcome
{
    Written,
    NotWritten,
    OutcomeUnknown,
}

/// <param name="IsActionAvailable">The documented SPI action exists on this build.</param>
/// <param name="IsReadable">The current value was read successfully.</param>
/// <param name="Value">The observed value; meaningful only when <paramref name="IsReadable"/> is true.</param>
public sealed record VisualEffectReading(bool IsActionAvailable, bool IsReadable, bool Value);

/// <summary>
/// Narrow adapter over the documented visual-effect system parameters. Injected so operation
/// behavior is provable without changing the developer's own Windows session.
/// </summary>
public interface IVisualEffectsAccess
{
    VisualEffectReading Read(VisualEffectSetting setting);

    VisualEffectWriteOutcome Write(VisualEffectSetting setting, bool value);
}

/// <summary>
/// Documented <c>SystemParametersInfoW</c> implementation. Reads are pure; writes update the user
/// profile and broadcast <c>WM_SETTINGCHANGE</c> so Explorer and running applications observe the
/// change without a restart. This adapter displays no UI.
/// </summary>
/// <remarks>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow
/// </remarks>
public sealed partial class WindowsVisualEffectsAccess : IVisualEffectsAccess
{
    private const uint SpiGetClientAreaAnimation = 0x1042;
    private const uint SpiSetClientAreaAnimation = 0x1043;
    private const uint SpiGetUiEffects = 0x103E;
    private const uint SpiSetUiEffects = 0x103F;

    private const uint SpifUpdateIniFile = 0x0001;
    private const uint SpifSendChange = 0x0002;

    /// <summary>ERROR_INVALID_PARAMETER: the running build does not implement the action.</summary>
    private const int ErrorInvalidParameter = 87;

    public VisualEffectReading Read(VisualEffectSetting setting)
    {
        var value = 0;
        if (GetSystemParameter(GetAction(setting), 0, ref value, 0))
        {
            return new VisualEffectReading(IsActionAvailable: true, IsReadable: true, Value: value != 0);
        }

        // Only ERROR_INVALID_PARAMETER distinguishes "this build has no such action" from a
        // transient read failure. Anything else keeps the action available but the state unknown,
        // which blocks mutation instead of guessing.
        var error = Marshal.GetLastPInvokeError();
        return new VisualEffectReading(
            IsActionAvailable: error != ErrorInvalidParameter,
            IsReadable: false,
            Value: false);
    }

    public VisualEffectWriteOutcome Write(VisualEffectSetting setting, bool value)
    {
        var succeeded = SetSystemParameter(
            SetAction(setting),
            0,
            value ? 1 : 0,
            SpifUpdateIniFile | SpifSendChange);
        if (succeeded)
        {
            return VisualEffectWriteOutcome.Written;
        }

        // A documented failure code means Windows rejected the call before changing the value.
        // A failure without a code is unattributable, so it is escalated rather than dismissed.
        return Marshal.GetLastPInvokeError() == 0
            ? VisualEffectWriteOutcome.OutcomeUnknown
            : VisualEffectWriteOutcome.NotWritten;
    }

    private static uint GetAction(VisualEffectSetting setting) => setting switch
    {
        VisualEffectSetting.ClientAreaAnimation => SpiGetClientAreaAnimation,
        VisualEffectSetting.UiEffects => SpiGetUiEffects,
        _ => throw new ArgumentOutOfRangeException(nameof(setting)),
    };

    private static uint SetAction(VisualEffectSetting setting) => setting switch
    {
        VisualEffectSetting.ClientAreaAnimation => SpiSetClientAreaAnimation,
        VisualEffectSetting.UiEffects => SpiSetUiEffects,
        _ => throw new ArgumentOutOfRangeException(nameof(setting)),
    };

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemParameter(
        uint uiAction,
        uint uiParam,
        ref int pvParam,
        uint fWinIni);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSystemParameter(
        uint uiAction,
        uint uiParam,
        nint pvParam,
        uint fWinIni);
}
