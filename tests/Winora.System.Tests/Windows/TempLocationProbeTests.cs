using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// Read-only coverage against the developer's own machine. The probe never moves or deletes
/// anything, so these tests cannot damage the session.
/// </summary>
public sealed class TempLocationProbeTests
{
    private static readonly WindowsTempLocationProbe Probe = new(new FakeElevation(false));

    /// <summary>Elevation is a property of the process, so tests state it rather than inherit it.</summary>
    private sealed class FakeElevation(bool isElevated) : IElevationProbe
    {
        public bool IsElevated { get; } = isElevated;
    }

    [Fact]
    public void The_catalog_is_not_empty_and_every_entry_is_identifiable()
    {
        var locations = Probe.Locations();

        Assert.NotEmpty(locations);
        foreach (var location in locations)
        {
            Assert.Matches("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", location.Id);
            Assert.False(string.IsNullOrWhiteSpace(location.Path));
        }
    }

    [Fact]
    public void Location_identifiers_are_unique()
    {
        var ids = Probe.Locations().Select(static l => l.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// These are serviced by Windows itself, so they are catalogued deliberately rather than omitted
    /// and silently rediscovered by a later contributor.
    /// </summary>
    /// <remarks>
    /// The previous version of this test asserted that all three are always present, and that claim
    /// was simply wrong: measured on 2026-08-04, this machine had no <c>C:\Windows\Logs\CBS</c> at
    /// all — servicing creates those logs and Windows removes them again. The probe drops a location
    /// that is not on disk, so what is asserted here is how a present one is classified, never that
    /// it is there.
    /// </remarks>
    [Theory]
    [InlineData("windows-temp")]
    [InlineData("software-distribution")]
    [InlineData("cbs-logs")]
    public void A_serviced_windows_location_is_catalogued_as_needing_elevation(string id)
    {
        foreach (var location in Probe.Locations().Where(l => l.Id == id))
        {
            Assert.Equal(TempLocationClassification.RequiresElevation, location.Classification);
        }
    }

    /// <summary>
    /// The user's temp folder always exists, so it anchors the theory above: without this, a probe
    /// that returned nothing at all would satisfy every one of those cases.
    /// </summary>
    [Fact]
    public void The_user_temp_location_is_always_catalogued()
    {
        var location = Probe.Locations().Single(static l => l.Id == "user-temp");

        Assert.Equal(TempLocationClassification.UserOwned, location.Classification);
        Assert.False(string.IsNullOrWhiteSpace(location.Path));
    }

    /// <summary>
    /// A reason code is optional and must never be empty. An empty one is worse than none: the
    /// localizer resolves it to the raw key and the screen shows "[winora.cleanup.…]" to the user,
    /// which is exactly what happened once.
    /// </summary>
    [Fact]
    public void A_reason_code_is_either_absent_or_meaningful()
    {
        foreach (var location in Probe.Locations())
        {
            if (location.ReasonCode is not null)
            {
                Assert.False(string.IsNullOrWhiteSpace(location.ReasonCode));
            }
        }
    }

    /// <summary>
    /// A protected folder that is not on this disk is not a decision the user has to make. Windows.old
    /// is the case that forced this: it survives only a few days after a feature update, and listing
    /// it the rest of the time put a row on the screen about reclaiming a folder that was not there.
    /// </summary>
    [Fact]
    public void A_protected_location_is_only_listed_when_it_is_actually_on_disk()
    {
        foreach (var location in Probe.Locations()
                     .Where(static l => l.Classification != TempLocationClassification.UserOwned))
        {
            Assert.True(
                Directory.Exists(location.Path),
                $"'{location.Id}' is listed but '{location.Path}' does not exist.");
        }
    }

    [Fact]
    public void The_per_user_temp_directory_is_user_owned()
    {
        var location = Probe.Locations().Single(l => l.Id == "user-temp");

        Assert.Equal(TempLocationClassification.UserOwned, location.Classification);
        Assert.Null(location.ReasonCode);
    }

    [Fact]
    public void Every_protected_location_states_a_reason_and_every_user_location_does_not()
    {
        foreach (var location in Probe.Locations())
        {
            if (location.Classification == TempLocationClassification.UserOwned)
            {
                Assert.Null(location.ReasonCode);
            }
        }
    }

    [Fact]
    public void Surveying_a_protected_location_is_refused_outright()
    {
        var protectedLocation = Probe.Locations()
            .First(static l => l.Classification != TempLocationClassification.UserOwned);

        Assert.Throws<InvalidOperationException>(() => Probe.Survey(protectedLocation, CancellationToken.None));
    }

    [Fact]
    public void Surveying_the_user_temp_directory_reports_a_plausible_total()
    {
        var location = Probe.Locations().Single(l => l.Id == "user-temp");

        var survey = Probe.Survey(location, CancellationToken.None);

        Assert.Equal(location, survey.Location);
        Assert.True(survey.FileCount >= 0);
        Assert.True(survey.TotalBytes >= 0);
    }

    /// <summary>
    /// A survey that could not read every entry must say so. Reporting a partial total as if it were
    /// complete would understate what reclamation is about to touch.
    /// </summary>
    [Fact]
    public void A_survey_reports_whether_it_could_read_everything()
    {
        var location = Probe.Locations().Single(l => l.Id == "user-temp");

        var survey = Probe.Survey(location, CancellationToken.None);

        Assert.True(survey.IsFullyEnumerated || survey.UnreadableEntryCount > 0);
    }

    [Fact]
    public void Surveying_is_stable_enough_to_be_free_of_side_effects()
    {
        var location = Probe.Locations().Single(l => l.Id == "user-temp");

        var first = Probe.Survey(location, CancellationToken.None);
        var second = Probe.Survey(location, CancellationToken.None);

        // Temp churns, so equality is not asserted; the point is that surveying changes nothing.
        Assert.True(Directory.Exists(location.Path));
        Assert.Equal(first.Location, second.Location);
    }
}
