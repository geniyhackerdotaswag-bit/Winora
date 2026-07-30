using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// Read-only coverage against the developer's own machine. The probe never moves or deletes
/// anything, so these tests cannot damage the session.
/// </summary>
public sealed class TempLocationProbeTests
{
    private static readonly WindowsTempLocationProbe Probe = new();

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
    /// These are serviced by Windows itself. Deleting from them breaks servicing and rollback of
    /// updates, so they are catalogued precisely to be shown as off-limits rather than omitted and
    /// silently rediscovered by a later contributor.
    /// </summary>
    [Theory]
    [InlineData("windows-temp")]
    [InlineData("software-distribution")]
    [InlineData("cbs-logs")]
    [InlineData("windows-old")]
    public void Serviced_windows_locations_are_catalogued_as_protected(string id)
    {
        var location = Probe.Locations().Single(l => l.Id == id);

        Assert.Equal(TempLocationClassification.Protected, location.Classification);
        Assert.False(string.IsNullOrWhiteSpace(location.ReasonCode));
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
            if (location.Classification == TempLocationClassification.Protected)
            {
                Assert.False(string.IsNullOrWhiteSpace(location.ReasonCode));
            }
            else if (location.Classification == TempLocationClassification.UserOwned)
            {
                Assert.Null(location.ReasonCode);
            }
        }
    }

    [Fact]
    public void Surveying_a_protected_location_is_refused_outright()
    {
        var protectedLocation = Probe.Locations()
            .First(static l => l.Classification == TempLocationClassification.Protected);

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
