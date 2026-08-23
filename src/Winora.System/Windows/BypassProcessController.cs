using System.Diagnostics;

namespace Winora.System.Windows;

/// <summary>What the bypass is doing, as Windows reports it.</summary>
public enum BypassState
{
    /// <summary>Nothing is running.</summary>
    Stopped,

    /// <summary>Winora's own copy is running.</summary>
    Running,

    /// <summary>
    /// A bypass from somewhere else is running. Winora reports it and leaves it alone.
    /// </summary>
    ForeignRunning,
}

/// <param name="State">What is running, if anything.</param>
/// <param name="ProcessId">The process, when one was found.</param>
/// <param name="ExecutablePath">Which copy is running, for the foreign case.</param>
public sealed record BypassStatus(BypassState State, int? ProcessId, string ExecutablePath);

/// <summary>How a start attempt ended.</summary>
public enum BypassStartOutcome
{
    /// <summary>The process is up and stayed up.</summary>
    Started,

    /// <summary>Something was already filtering; a second filter is never stacked on a first.</summary>
    AlreadyRunning,

    /// <summary><c>winws.exe</c> is not where the release should have put it.</summary>
    Missing,

    /// <summary>Windows refused to start it at all — most often an antivirus quarantine.</summary>
    Refused,

    /// <summary>
    /// It started and exited within the settling window, so nothing is being filtered.
    /// </summary>
    ExitedImmediately,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Detail">
/// Whatever the tool said on its way out, or the exit code. Empty when there was nothing to say.
/// </param>
public sealed record BypassStartReport(BypassStartOutcome Outcome, string Detail)
{
    public bool Started => Outcome is BypassStartOutcome.Started;
}

/// <summary>Starts and stops the bypass, and reports what is actually running.</summary>
public interface IBypassProcessController
{
    /// <summary>Asks Windows what is running. Never a remembered value.</summary>
    BypassStatus Status();

    /// <summary>Starts the strategy hidden, and reports whether it survived.</summary>
    BypassStartReport Start(BypassStrategy strategy);

    /// <summary>Stops Winora's own copy. Never touches a foreign one.</summary>
    bool Stop();
}

/// <summary>
/// The bypass process.
/// </summary>
/// <remarks>
/// <para>
/// State is read from the running process list every time it is asked for, never cached. The bypass
/// outlives the app by design — closing Winora leaves it running — so a remembered flag would say
/// "off" on the next launch while the network was still being filtered. That is the same class of
/// lie the registry domains had, and it matters more here: an invisible network filter nobody
/// remembers turning on is a problem that surfaces weeks later as "the internet is strange".
/// </para>
/// <para>
/// Ownership is decided by executable path, not process name. Another launcher for the same tool
/// runs an identically named process, and matching on the name alone would let Winora offer to stop
/// something it did not start.
/// </para>
/// </remarks>
public sealed class BypassProcessController : IBypassProcessController
{
    /// <summary>Without the extension, which is how the process list names it.</summary>
    private const string ProcessName = "winws";

    /// <summary>
    /// How long a start is watched before it counts as having stayed up.
    /// </summary>
    /// <remarks>
    /// The failures worth catching are immediate — a missing list, a rejected argument, a driver
    /// that will not load — and all of them land well inside this. Long enough to be sure, short
    /// enough that a working start does not feel like it hung.
    /// </remarks>
    private const int SettlingMilliseconds = 1500;

    private readonly IBypassStrategyCatalog _catalog;
    private readonly string _ownExecutable;

    public BypassProcessController()
        : this(new BypassStrategyCatalog())
    {
    }

    public BypassProcessController(IBypassStrategyCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _ownExecutable = Path.Combine(catalog.RootDirectory, "bin", "winws.exe");
    }

    public BypassStatus Status()
    {
        Process[] running;
        try
        {
            running = Process.GetProcessesByName(ProcessName);
        }
        catch (Exception)
        {
            return new BypassStatus(BypassState.Stopped, null, string.Empty);
        }

        BypassStatus? foreign = null;

        try
        {
            foreach (var process in running)
            {
                var path = PathOf(process);

                if (string.Equals(path, _ownExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    return new BypassStatus(BypassState.Running, process.Id, path);
                }

                // Remembered but not returned yet: our own copy takes precedence if it is also up.
                foreign ??= new BypassStatus(BypassState.ForeignRunning, process.Id, path);
            }
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }

        return foreign ?? new BypassStatus(BypassState.Stopped, null, string.Empty);
    }

    /// <summary>
    /// Starts a strategy and waits long enough to know whether it stayed up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reporting that <c>CreateProcess</c> succeeded is not reporting that the bypass is running.
    /// Every strategy failed this way once: the process started, exited inside a second because a
    /// host list it needed did not exist, and the screen went back to "not running" with nothing
    /// anywhere to say why. A start that cannot say what happened is the same kind of verification
    /// that lies this project exists to avoid.
    /// </para>
    /// <para>
    /// Standard error is captured for the same reason — it is where the tool says which file it
    /// could not open. Standard output is left alone: it is chatty, it is not where errors go, and
    /// reading only one stream cannot deadlock.
    /// </para>
    /// </remarks>
    public BypassStartReport Start(BypassStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        // Never a second one on top of a first. Two packet filters on the same traffic is how a
        // working connection becomes an unexplainable one.
        if (Status().State != BypassState.Stopped)
        {
            return new BypassStartReport(BypassStartOutcome.AlreadyRunning, string.Empty);
        }

        if (!File.Exists(strategy.ExecutablePath))
        {
            return new BypassStartReport(BypassStartOutcome.Missing, strategy.ExecutablePath);
        }

        // The lists the launch line references: the ones the release's own script creates, without
        // which winws.exe exits immediately, and Winora's own host list, which replaces the
        // release's. See BypassStrategyCatalog.PrepareLists.
        _catalog.PrepareLists();

        try
        {
            var startInfo = new ProcessStartInfo(strategy.ExecutablePath)
            {
                WorkingDirectory = strategy.WorkingDirectory,

                // No console window. The tool is a background filter and its window would be one
                // more thing on screen that the user cannot usefully do anything with.
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
            };

            foreach (var argument in strategy.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var started = Process.Start(startInfo);
            if (started is null)
            {
                return new BypassStartReport(BypassStartOutcome.Refused, string.Empty);
            }

            if (!started.WaitForExit(SettlingMilliseconds))
            {
                return new BypassStartReport(BypassStartOutcome.Started, string.Empty);
            }

            var complaint = ReadComplaint(started);
            return new BypassStartReport(
                BypassStartOutcome.ExitedImmediately,
                complaint.Length > 0 ? complaint : $"Код выхода {started.ExitCode}.");
        }
        catch (Exception exception)
        {
            // Antivirus quarantine is the common cause here, and it is normal for this class of
            // tool. The screen says so rather than leaving the failure unexplained.
            return new BypassStartReport(BypassStartOutcome.Refused, exception.Message);
        }
    }

    public bool Stop()
    {
        var status = Status();

        // A foreign bypass belongs to whatever started it. Winora reports it and stops there.
        if (status.State != BypassState.Running || status.ProcessId is not { } id)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The last thing the tool said before exiting, trimmed to something a screen can hold.
    /// </summary>
    private static string ReadComplaint(Process process)
    {
        try
        {
            var text = process.StandardError.ReadToEnd().Trim();
            return text.Length <= 400 ? text : text[..400] + "…";
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <remarks>
    /// The full path of a running process needs the right to read its module list. Winora runs
    /// elevated so this normally succeeds; when it does not, an unknown path is treated as foreign,
    /// which is the cautious direction — it means Winora will not offer to stop it.
    /// </remarks>
    private static string PathOf(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
