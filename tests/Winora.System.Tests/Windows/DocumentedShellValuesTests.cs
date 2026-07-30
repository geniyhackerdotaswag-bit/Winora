using Microsoft.Win32;
using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// The catalog is the whole safety boundary for this domain: it decides which registry values
/// Winora will touch at all. Everything outside it must stay unreachable, not merely undocumented.
/// </summary>
public sealed class DocumentedShellValuesTests
{
    [Fact]
    public void The_catalog_is_not_empty_and_every_entry_is_documented()
    {
        Assert.NotEmpty(DocumentedShellValues.All);
        foreach (var entry in DocumentedShellValues.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.ValueName));
            Assert.NotEmpty(entry.AllowedValues);
            Assert.Equal(RegistryValueKind.DWord, entry.DocumentedKind);
            Assert.True(
                entry.Documentation.IsAbsoluteUri,
                $"{entry.ValueName} must carry an absolute Microsoft Learn URI.");
        }
    }

    [Fact]
    public void Value_names_are_unique()
    {
        var names = DocumentedShellValues.All.Select(static e => e.ValueName).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// Microsoft either documents these as opaque, calls them undocumented, or has disabled them.
    /// They must never resolve to a writable entry, however plausible they look.
    /// </summary>
    [Theory]
    [InlineData("TaskbarSi")]
    [InlineData("StuckRects3")]
    [InlineData("FavoritesMigration")]
    [InlineData("UserPreferencesMask")]
    [InlineData("VisualFXSetting")]
    public void Explicitly_excluded_values_are_absent_from_the_catalog(string valueName)
    {
        Assert.False(DocumentedShellValues.TryFind(valueName, out _));
    }

    [Fact]
    public void Every_entry_declares_how_the_change_becomes_visible()
    {
        // Winora never restarts Explorer, so it must state what the user has to do instead.
        foreach (var entry in DocumentedShellValues.All)
        {
            Assert.True(
                entry.Restart is Core.Changes.RestartRequirement.Explorer
                    or Core.Changes.RestartRequirement.SignOut,
                $"{entry.ValueName} must say whether Explorer or a sign-out is needed.");
        }
    }

    [Fact]
    public void Operation_identifiers_are_stable_unique_and_display_safe()
    {
        var ids = DocumentedShellValues.All.Select(static e => e.OperationId).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var id in ids)
        {
            Assert.StartsWith("winora.shell.", id, StringComparison.Ordinal);
        }

        var stepIds = DocumentedShellValues.All.Select(static e => e.StepId).ToArray();
        Assert.Equal(stepIds.Length, stepIds.Distinct(StringComparer.Ordinal).Count());
        foreach (var stepId in stepIds)
        {
            Assert.Matches("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", stepId);
        }
    }

    [Fact]
    public void An_unknown_value_name_is_refused_rather_than_guessed()
    {
        Assert.False(DocumentedShellValues.TryFind("NoSuchValue", out _));
        Assert.Throws<KeyNotFoundException>(() => DocumentedShellValues.Find("NoSuchValue"));
    }
}
