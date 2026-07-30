using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// Read-only coverage against the developer's own live registry. These tests never write, so they
/// cannot change the running session.
/// </summary>
public sealed class UserShellPreferenceAccessTests
{
    public static TheoryData<string> AllValueNames()
    {
        var data = new TheoryData<string>();
        foreach (var entry in DocumentedShellValues.All)
        {
            data.Add(entry.ValueName);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllValueNames))]
    public void Every_documented_value_reads_without_throwing(string valueName)
    {
        var reading = new WindowsUserShellPreferenceAccess().Read(DocumentedShellValues.Find(valueName));

        Assert.True(reading.IsKeyAccessible, $"The Explorer\\Advanced key was unreachable for {valueName}.");
    }

    /// <summary>
    /// Most of these values do not exist until something writes them; Windows then applies its own
    /// default. Absence is a distinct state, not a zero, because restoring it means deleting the
    /// value rather than writing a number Winora guessed.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllValueNames))]
    public void An_absent_value_is_reported_as_absent_rather_than_as_zero(string valueName)
    {
        var reading = new WindowsUserShellPreferenceAccess().Read(DocumentedShellValues.Find(valueName));

        if (!reading.IsValuePresent)
        {
            Assert.Null(reading.Value);
            Assert.True(reading.IsKindAsDocumented, "An absent value cannot contradict its documented kind.");
        }
        else
        {
            Assert.NotNull(reading.Value);
        }
    }

    [Theory]
    [MemberData(nameof(AllValueNames))]
    public void Reading_is_stable_and_free_of_side_effects(string valueName)
    {
        var access = new WindowsUserShellPreferenceAccess();
        var entry = DocumentedShellValues.Find(valueName);

        Assert.Equal(access.Read(entry), access.Read(entry));
    }

    [Theory]
    [MemberData(nameof(AllValueNames))]
    public void Write_access_is_probed_rather_than_inferred_from_readability(string valueName)
    {
        var reading = new WindowsUserShellPreferenceAccess().Read(DocumentedShellValues.Find(valueName));

        // Explorer\Advanced is normally writable by its owner, so on an unmanaged profile this is
        // true; the point is that the adapter asked the question instead of assuming the answer.
        Assert.True(reading.IsKeyWritable, "The Explorer\\Advanced key reported as not writable.");
    }

    [Fact]
    public void A_present_value_of_the_wrong_kind_is_reported_as_undocumented_rather_than_coerced()
    {
        // The Windows 11 settings reference lists these as REG_SZ under SystemSettings_* names while
        // the live registry uses plain DWORDs. A probe that coerced whatever it found would write
        // back the wrong shape, so the mismatch has to be visible in the reading itself.
        var reading = new ShellPreferenceReading(
            IsKeyAccessible: true,
            IsValuePresent: true,
            Value: null,
            IsKindAsDocumented: false);

        Assert.False(reading.IsUsable);
    }

    [Fact]
    public void An_absent_value_is_usable_because_windows_applies_a_documented_default()
    {
        var reading = new ShellPreferenceReading(
            IsKeyAccessible: true,
            IsValuePresent: false,
            Value: null,
            IsKindAsDocumented: true);

        Assert.True(reading.IsUsable);
    }

    [Fact]
    public void An_unreachable_key_is_never_usable()
    {
        var reading = new ShellPreferenceReading(
            IsKeyAccessible: false,
            IsValuePresent: false,
            Value: null,
            IsKindAsDocumented: true);

        Assert.False(reading.IsUsable);
    }
}
