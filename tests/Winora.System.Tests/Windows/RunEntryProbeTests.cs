using System.Text;
using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// Read-only coverage against the developer's own machine. Nothing here writes, so the running
/// session cannot be changed by these tests.
/// </summary>
public sealed class RunEntryProbeTests
{
    private static readonly IReadOnlyList<RunEntry> Entries = new WindowsRunEntryProbe().Read();

    [Fact]
    public void Reading_does_not_throw_and_returns_a_list()
    {
        Assert.NotNull(Entries);
    }

    [Fact]
    public void Every_entry_has_a_name()
    {
        foreach (var entry in Entries)
        {
            Assert.False(string.IsNullOrEmpty(entry.Name));
        }
    }

    [Fact]
    public void An_entry_of_an_undocumented_kind_reports_no_command_rather_than_a_guess()
    {
        foreach (var entry in Entries.Where(static e => !e.IsDocumentedKind))
        {
            Assert.Equal(string.Empty, entry.Command);
        }
    }

    [Fact]
    public void Documented_entries_carry_their_command()
    {
        foreach (var entry in Entries.Where(static e => e.IsDocumentedKind))
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Command));
        }
    }

    [Fact]
    public void Entries_are_attributed_to_the_key_they_came_from()
    {
        foreach (var entry in Entries)
        {
            Assert.True(
                entry.Scope is RunEntryScope.CurrentUser or RunEntryScope.LocalMachine,
                $"{entry.Name} has no scope.");
        }
    }

    [Fact]
    public void Reading_is_stable_and_free_of_side_effects()
    {
        var second = new WindowsRunEntryProbe().Read();

        Assert.Equal(
            Entries.Select(static e => (e.Name, e.Scope)).OrderBy(static x => x.Name, StringComparer.Ordinal),
            second.Select(static e => (e.Name, e.Scope)).OrderBy(static x => x.Name, StringComparer.Ordinal));
    }

    /// <summary>
    /// Pins the constraint that blocks the mutating half of this domain, so a later contributor does
    /// not reach for hex-encoding and discover the ceiling the hard way. Catalog operation ids are
    /// capped at 96 lowercase characters; after the domain prefix, roughly 38 bytes of name fit as
    /// hex. Real browser auto-launch entries are longer than that.
    /// </summary>
    [Fact]
    public void A_real_entry_name_does_not_fit_in_an_operation_id_as_hex()
    {
        const string prefix = "winora.startup.run.v";
        const string observed = "YandexBrowserAutoLaunch_EFB5B37C64649EA7404EBBBAEC96AF4B";

        var encodedLength = prefix.Length + (Encoding.UTF8.GetByteCount(observed) * 2);

        Assert.True(
            encodedLength > 96,
            $"Hex-encoding this name yields {encodedLength} characters, which would have fit after all.");
    }
}
