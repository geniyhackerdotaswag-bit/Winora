using System.Diagnostics;
using Winora.System.Windows;

namespace Winora.App.Services;

/// <summary>
/// Restarts Winora with administrative rights when it was started without them.
/// </summary>
/// <remarks>
/// <para>
/// The owner's decision, taken on 2026-08-02 after the point had been raised three times: Winora
/// runs elevated, always. The earlier rule that the main window stays at medium integrity was
/// written on a belief that turned out to be false — that a packaged MSIX app cannot be elevated at
/// all. It can: launching <c>shell:AppsFolder\{AUMID}</c> with the <c>runas</c> verb produces a
/// process whose token reports elevation, measured on this machine.
/// </para>
/// <para>
/// The cost is real and is accepted deliberately: an elevated window that deletes files has a wider
/// blast radius than a medium-integrity one, and every safety check in this project matters more
/// because of it, not less.
/// </para>
/// <para>
/// Relaunch is attempted exactly once. If the user dismisses the consent prompt, Winora carries on
/// unelevated rather than looping or exiting: the per-user domains all still work, and the screens
/// state what they cannot reach.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shellexecuteexw
/// </remarks>
public interface IElevationRelauncher
{
    /// <summary>
    /// True when a relaunch was started and this process should exit without showing a window.
    /// </summary>
    bool TryRelaunchElevated();
}

/// <inheritdoc />
public sealed class ElevationRelauncher : IElevationRelauncher
{
    private readonly IElevationProbe _elevation;
    private readonly IDeploymentState _deployment;
    private readonly IPackageIdentityAccessor _identity;

    public ElevationRelauncher(
        IElevationProbe elevation,
        IDeploymentState deployment,
        IPackageIdentityAccessor identity)
    {
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
        _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    /// <summary>
    /// Debug-only escape hatch: a marker file sitting next to the app's own binaries.
    /// <para>
    /// An elevated window cannot be screenshotted, driven by UI Automation, or closed from a normal
    /// session, which removes every automated way of checking the interface. This keeps a Debug
    /// build at medium integrity for that work.
    /// </para>
    /// <para>
    /// A file rather than an environment variable: a packaged app is started by the shell, so it
    /// inherits the shell's environment from sign-in and never sees a variable set afterwards. The
    /// first attempt at this hatch used a variable and would silently have done nothing.
    /// </para>
    /// <para>
    /// Compiled out of Release entirely, so a shipped Winora has no way to skip elevation and the
    /// marker file means nothing there.
    /// </para>
    /// </summary>
    private const string SkipMarkerFileName = "WINORA_NO_AUTO_ELEVATE";

    public bool TryRelaunchElevated()
    {
        if (_elevation.IsElevated || !_deployment.IsPackaged)
        {
            return false;
        }

#if DEBUG
        if (SkipMarkerIsPresent())
        {
            return false;
        }
#endif

        if (_identity.TryGetApplicationUserModelId(out var aumid))
        {
            try
            {
                // UseShellExecute with the runas verb is what raises the consent prompt; the
                // AppsFolder moniker is how a packaged app is addressed by the shell.
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"shell:AppsFolder\\{aumid}",
                    UseShellExecute = true,
                    Verb = "runas",
                });
                return true;
            }
            catch (Exception)
            {
                // A declined prompt throws. Carrying on unelevated is the honest fallback: the
                // per-user domains work, and the screens already say what needs administrator.
                return false;
            }
        }

        return false;
    }

#if DEBUG
    private static bool SkipMarkerIsPresent()
    {
        try
        {
            var directory = AppContext.BaseDirectory;
            return File.Exists(Path.Combine(directory, SkipMarkerFileName));
        }
        catch (Exception)
        {
            return false;
        }
    }
#endif
}
