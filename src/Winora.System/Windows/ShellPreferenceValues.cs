using System.Globalization;

namespace Winora.System.Windows;

/// <summary>
/// The canonical value vocabulary for a documented Explorer preference. These are stable
/// identifiers, not user-facing text.
/// </summary>
/// <remarks>
/// <see cref="Unset"/> is a first-class value, not a missing one. Most of these registry values do
/// not exist until something writes them, and Windows then applies its own default. Treating
/// absence as zero would make rollback write a number Winora invented instead of restoring the
/// state the user actually had.
/// </remarks>
public static class ShellPreferenceValues
{
    public const string Kind = "winora.value.shell-preference";

    public const string Unset = "unset";

    public static string For(int? value) =>
        value is null ? Unset : value.Value.ToString(CultureInfo.InvariantCulture);

    public static string For(ShellPreferenceReading reading) =>
        reading.IsValuePresent ? For(reading.Value) : Unset;

    public static bool TryParse(string? text, out int? value)
    {
        if (StringComparer.Ordinal.Equals(text, Unset))
        {
            value = null;
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }
}
