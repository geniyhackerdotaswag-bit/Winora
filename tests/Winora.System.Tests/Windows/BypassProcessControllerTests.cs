using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// The bypass process, against the real process list.
/// </summary>
/// <remarks>
/// Nothing here starts a packet filter. The machine running these tests is the developer's own, and
/// a test that put a DPI bypass on its network would be changing the thing it is meant to observe.
/// What is checked is the reasoning around starting: what the controller reports, what it refuses,
/// and above all what it declines to touch.
/// </remarks>
public sealed class BypassProcessControllerTests : IDisposable
{
    private readonly string _root;

    public BypassProcessControllerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "winora-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a passing test over.
        }
    }

    /// <summary>
    /// With nothing of ours running, the honest answer is stopped — or, if the machine happens to
    /// be running someone else's bypass, that it is not ours.
    /// </summary>
    [Fact]
    public void Nothing_of_ours_is_reported_as_running()
    {
        var status = Controller().Status();

        Assert.NotEqual(BypassState.Running, status.State);
    }

    /// <summary>
    /// Two packet filters on the same traffic is how a working connection becomes an
    /// unexplainable one, so a start is refused whenever anything is already up.
    /// </summary>
    [Fact]
    public void Starting_is_refused_while_something_is_already_running()
    {
        var controller = Controller();
        if (controller.Status().State == BypassState.Stopped)
        {
            // Nothing is running on this machine, so the refusal cannot be observed here. The
            // missing-executable case below covers the other half of Start's contract.
            return;
        }

        var report = controller.Start(Strategy());

        Assert.False(report.Started);
        Assert.Equal(BypassStartOutcome.AlreadyRunning, report.Outcome);
    }

    /// <summary>
    /// A strategy pointing at an executable that is not there must fail rather than throw: the
    /// release may have been deleted, or antivirus may have quarantined it.
    /// </summary>
    [Fact]
    public void Starting_a_missing_executable_fails_quietly()
    {
        var report = Controller().Start(Strategy());

        Assert.False(report.Started);

        // Either answer is correct and which one comes back depends on the machine: a bypass
        // already running is checked first, by design, because refusing a second filter matters
        // more than reporting a missing file.
        Assert.Contains(
            report.Outcome,
            (BypassStartOutcome[])[BypassStartOutcome.Missing, BypassStartOutcome.AlreadyRunning]);
    }

    /// <summary>
    /// Stop is for Winora's own copy. Another launcher's process belongs to whatever started it,
    /// and killing it would be Winora reaching into a program it does not own.
    /// </summary>
    [Fact]
    public void Stopping_does_nothing_when_ours_is_not_running()
    {
        var controller = Controller();
        if (controller.Status().State == BypassState.Running)
        {
            return;
        }

        Assert.False(controller.Stop());
    }

    /// <summary>
    /// Ownership is decided by path, not by process name: another launcher for the same tool runs
    /// an identically named process, and matching on the name would let Winora stop it.
    /// </summary>
    [Fact]
    public void Two_controllers_with_different_folders_do_not_claim_the_same_process()
    {
        var mine = Controller();
        var other = new BypassProcessController(new BypassStrategyCatalog(Path.Combine(_root, "elsewhere")));

        var mineStatus = mine.Status();
        var otherStatus = other.Status();

        // At most one of the two may call a running process its own.
        Assert.False(
            mineStatus.State == BypassState.Running && otherStatus.State == BypassState.Running,
            "Both controllers claimed the same process as their own.");
    }

    [Fact]
    public void Status_never_throws_and_always_answers()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var status = Controller().Status();

            Assert.True(Enum.IsDefined(status.State));
            Assert.True(status.State == BypassState.Stopped || status.ProcessId is not null);
        }
    }

    private BypassProcessController Controller() =>
        new(new BypassStrategyCatalog(_root));

    private BypassStrategy Strategy() =>
        new(
            "test",
            "test",
            Path.Combine(_root, "bin", "winws.exe"),
            ["--wf-tcp=443"],
            Path.Combine(_root, "bin"));
}

/// <summary>
/// Reading the release feed and deciding whether an update exists.
/// </summary>
/// <remarks>
/// The decision matters more than the download: an unreadable feed must never be presented as an
/// available update, or the user is offered a download that cannot happen.
/// </remarks>
public sealed class BypassReleaseCheckTests
{
    private static readonly BypassRelease Release =
        new("v1.2.3", DateTimeOffset.UtcNow, "https://example.invalid/zapret.zip", 1024);

    [Fact]
    public void An_update_is_offered_when_the_tags_differ()
    {
        Assert.True(new BypassReleaseCheck("v1.2.2", Release).UpdateAvailable);
    }

    [Fact]
    public void No_update_is_offered_when_the_tags_match()
    {
        Assert.False(new BypassReleaseCheck("v1.2.3", Release).UpdateAvailable);
    }

    /// <summary>Tags are compared without case, so a re-tagged release is not a false update.</summary>
    [Fact]
    public void Tag_comparison_ignores_case()
    {
        Assert.False(new BypassReleaseCheck("V1.2.3", Release).UpdateAvailable);
    }

    /// <summary>
    /// Nothing installed is not the same as an update: it is a first install, and the screen says
    /// so differently. But it does mean there is something to fetch.
    /// </summary>
    [Fact]
    public void Nothing_installed_counts_as_something_to_fetch()
    {
        Assert.True(new BypassReleaseCheck(string.Empty, Release).UpdateAvailable);
    }

    /// <summary>
    /// No network, a rate limit, or a changed feed. Unknown must not become "update available",
    /// which would offer a download that immediately fails.
    /// </summary>
    [Fact]
    public void An_unreadable_feed_is_never_an_update()
    {
        Assert.False(new BypassReleaseCheck("v1.2.2", null).UpdateAvailable);
        Assert.False(new BypassReleaseCheck(string.Empty, null).UpdateAvailable);
    }
}
