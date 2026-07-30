using Winora.App.Navigation;
using Winora.App.ViewModels;
using Winora.Core.Changes;
using Xunit;

namespace Winora.App.Tests.Architecture;

/// <summary>
/// The ViewModel *dependency* boundary is enforced by <c>Winora.Architecture.Tests</c> against source
/// text, because reflection over the WinUI assembly cannot run in this host: its CsWinRT module
/// initializer performs COM activation and fails with REGDB_E_CLASSNOTREG outside a WinUI process.
/// What remains here is the presentation contract that must hold for the UI to have no dead clicks.
/// </summary>
public sealed class ViewModelBoundaryTests
{
    /// <summary>
    /// The coordinator can return fifteen dispositions. If even one has no presentation, the user
    /// gets a click that appears to do nothing. This is the test that forbids that.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllDispositions))]
    public void Every_coordinator_disposition_has_a_message_and_a_destination(CoordinatorDisposition disposition)
    {
        var resourceKey = CoordinatorDispositionPresentation.ResourceKeyFor(disposition);
        Assert.False(
            string.IsNullOrWhiteSpace(resourceKey),
            $"{disposition} has no user-facing message resource key.");

        var route = CoordinatorDispositionPresentation.RouteFor(disposition);
        Assert.True(
            RouteRegistry.Create().TryFind(route, out _),
            $"{disposition} routes to '{route}', which is not registered.");
    }

    public static TheoryData<CoordinatorDisposition> AllDispositions()
    {
        var data = new TheoryData<CoordinatorDisposition>();
        foreach (var disposition in Enum.GetValues<CoordinatorDisposition>())
        {
            data.Add(disposition);
        }

        return data;
    }

    [Fact]
    public void Every_disposition_has_a_distinct_message()
    {
        var keys = Enum.GetValues<CoordinatorDisposition>()
            .Select(CoordinatorDispositionPresentation.ResourceKeyFor)
            .ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_four_failure_stages_are_never_collapsed_into_one_generic_error()
    {
        // Backup, apply, verification, rollback, and durability are separate stages by specification
        // section 14. Collapsing them would hide which stage the user must act on.
        var failureKeys = new[]
        {
            CoordinatorDisposition.BackupFailed,
            CoordinatorDisposition.ApplyFailed,
            CoordinatorDisposition.VerificationFailed,
            CoordinatorDisposition.RollbackFailed,
            CoordinatorDisposition.DurabilityFailure,
        }.Select(CoordinatorDispositionPresentation.ResourceKeyFor).ToArray();

        Assert.Equal(failureKeys.Length, failureKeys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_busy_lease_never_routes_to_a_failure_screen()
    {
        // OperationBusy means another Winora session holds the lease. Nothing was written and no UAC
        // was raised, so it is informational, not a failure.
        Assert.Equal(
            RouteKeys.Applying,
            CoordinatorDispositionPresentation.RouteFor(CoordinatorDisposition.OperationBusy));
    }

    [Fact]
    public void Recovery_required_dispositions_route_to_the_recovery_screen()
    {
        foreach (var disposition in new[]
                 {
                     CoordinatorDisposition.PartialRecoveryRequired,
                     CoordinatorDisposition.Conflict,
                     CoordinatorDisposition.DurabilityFailure,
                 })
        {
            Assert.Equal(
                RouteKeys.Recovery,
                CoordinatorDispositionPresentation.RouteFor(disposition));
        }
    }

    [Fact]
    public void A_successful_outcome_never_routes_to_a_failure_screen()
    {
        foreach (var disposition in new[]
                 {
                     CoordinatorDisposition.Completed,
                     CoordinatorDisposition.RolledBack,
                     CoordinatorDisposition.AlreadyRestored,
                     CoordinatorDisposition.Reconciled,
                 })
        {
            Assert.Equal(
                RouteKeys.ResultSuccess,
                CoordinatorDispositionPresentation.RouteFor(disposition));
        }
    }

    [Fact]
    public void An_unknown_disposition_value_throws_rather_than_showing_a_blank_screen()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CoordinatorDispositionPresentation.ResourceKeyFor((CoordinatorDisposition)9999));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CoordinatorDispositionPresentation.RouteFor((CoordinatorDisposition)9999));
    }
}
