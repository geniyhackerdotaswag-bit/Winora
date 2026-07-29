namespace Winora.System.Windows;

/// <summary>
/// Read-only probe for the running Windows version. Injected so that capability policy can be
/// exercised without depending on the host operating system.
/// </summary>
public interface IWindowsBuildProbe
{
    WindowsBuildFacts Read();
}

/// <summary>
/// Reads the documented operating-system version. On .NET, <see cref="Environment.OSVersion"/>
/// reports the real version obtained from the kernel rather than an application-manifest shim,
/// so no additional native call is required and nothing is written.
/// </summary>
/// <remarks>
/// Microsoft Learn: https://learn.microsoft.com/en-us/dotnet/api/system.environment.osversion
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-rtlgetversion
/// </remarks>
public sealed class WindowsBuildProbe : IWindowsBuildProbe
{
    public WindowsBuildFacts Read()
    {
        var version = Environment.OSVersion.Version;

        return new WindowsBuildFacts(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0));
    }
}
