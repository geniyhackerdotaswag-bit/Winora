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
        // Two domains now, and the prefix is how each screen finds its own rows. Anything already
        // written to a journal or a saved plan carries "winora.shell." and must keep resolving, so
        // that prefix is asserted to still exist rather than merely to be allowed.
        foreach (var id in ids)
        {
            Assert.Matches("^winora\\.(shell|explorer)\\.[a-z0-9-]+$", id);
        }

        Assert.Contains(ids, static id => id.StartsWith("winora.shell.", StringComparison.Ordinal));
        Assert.Contains(ids, static id => id.StartsWith("winora.explorer.", StringComparison.Ordinal));

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

    /// <summary>
    /// The File Explorer values, and the numbers that were measured rather than read.
    /// </summary>
    /// <remarks>
    /// Microsoft's own specification describes <c>hideFileExt</c> as "Displays known file
    /// extensions. MUST be 1 to enable", which reads as the opposite of what a value called
    /// <em>Hide</em>FileExt does. Asking the shell through <c>SHGetSettings</c> on 2026-08-27
    /// settled it: <c>HideFileExt=0</c> gave <c>fShowExtensions</c> true, <c>Hidden=2</c> gave
    /// <c>fShowAllObjects</c> false. A wrong mapping here would invert both switches on screen.
    /// </remarks>
    [Fact]
    public void The_file_explorer_values_carry_the_numbers_that_were_measured()
    {
        var extensions = DocumentedShellValues.Find("HideFileExt");
        Assert.Equal("winora.explorer.file-extensions", extensions.OperationId);
        Assert.Equal([0, 1], extensions.AllowedValues);
        Assert.Equal(1, extensions.DefaultValue);

        var hidden = DocumentedShellValues.Find("Hidden");
        Assert.Equal("winora.explorer.hidden-files", hidden.OperationId);
        Assert.Equal([1, 2], hidden.AllowedValues);
        Assert.Equal(2, hidden.DefaultValue);
    }

    /// <summary>
    /// Protected operating system files are not offered.
    /// </summary>
    /// <remarks>
    /// ShowSuperHidden sits on the same documentation page and is deliberately absent. Showing
    /// those files is a way to delete something Windows depends on, and Microsoft's own dialog puts
    /// a warning in front of it. Beside "show file extensions" it would read as the same kind of
    /// harmless choice, which is exactly what it is not.
    /// </remarks>
    [Fact]
    public void Protected_operating_system_files_are_not_on_offer()
    {
        Assert.False(DocumentedShellValues.TryFind("ShowSuperHidden", out _));
    }
}
