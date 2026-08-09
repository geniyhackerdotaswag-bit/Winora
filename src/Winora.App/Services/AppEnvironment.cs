using System.Reflection;
using Winora.Infrastructure.Paths;
using Winora.System.Windows;

namespace Winora.App.Services;

/// <summary>Facts about this installation that a user may need to report or check.</summary>
public interface IAppEnvironment
{
    string Version { get; }

    bool IsElevated { get; }

    /// <summary>Where Winora keeps its journal, plan archive and backups.</summary>
    string StorageRoot { get; }
}

/// <inheritdoc />
public sealed class AppEnvironment : IAppEnvironment
{
    private readonly IElevationProbe _elevation;
    private readonly IDeploymentState _deployment;

    public AppEnvironment(IElevationProbe elevation, IDeploymentState deployment)
    {
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
        _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
    }

    /// <remarks>
    /// The package version when there is one, because that is the number the user was asked to
    /// install and the one they would quote. An unpackaged development launch has no package
    /// identity and falls back to the assembly version rather than throwing.
    /// </remarks>
    public string Version
    {
        get
        {
            if (_deployment.IsPackaged)
            {
                try
                {
                    var version = global::Windows.ApplicationModel.Package.Current.Id.Version;
                    return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
                }
                catch (Exception)
                {
                    // Falls through to the assembly version below.
                }
            }

            return typeof(AppEnvironment).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(AppEnvironment).Assembly.GetName().Version?.ToString()
                ?? string.Empty;
        }
    }

    public bool IsElevated => _elevation.IsElevated;

    public string StorageRoot => WinoraDataPaths.RootForCurrentUser();
}
