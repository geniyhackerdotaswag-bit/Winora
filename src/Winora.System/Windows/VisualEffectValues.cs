namespace Winora.System.Windows;

/// <summary>
/// The canonical value vocabulary for a two-state visual-effect preference. These are stable
/// identifiers, not user-facing text: <c>Winora.App</c> renders them through its localized
/// resources, and the action journal stores the identifier.
/// </summary>
public static class VisualEffectValues
{
    public const string Kind = "winora.value.toggle";

    public const string On = "on";

    public const string Off = "off";

    public static string For(bool value) => value ? On : Off;

    public static bool TryParse(string? text, out bool value)
    {
        switch (text)
        {
            case On:
                value = true;
                return true;
            case Off:
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }
}
