using Microsoft.Extensions.DependencyInjection;
using Winora.App.Navigation;
using Winora.App.ViewModels;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Infrastructure.Backups;
using Winora.Infrastructure.Journal;
using Winora.Infrastructure.Leases;
using Winora.Infrastructure.Operations;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;
using Winora.Infrastructure.Time;
using Winora.System.Backups;
using Winora.System.Operations;
using Winora.System.Windows;

namespace Winora.App.Services;

/// <summary>The single composition root. Nothing else constructs infrastructure or system types.</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddWinora(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddShell(services);
        AddOperations(services);
        AddPersistence(services);
        return services;
    }

    private static void AddShell(IServiceCollection services)
    {
        services.AddSingleton(_ => RouteRegistry.Create());
        services.AddSingleton<IDeploymentState, DeploymentState>();
        services.AddSingleton<ILocalizationService, ResourceLocalizationService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(provider => provider.GetRequiredService<NavigationService>());

        services.AddSingleton<ShellViewModel>();
        services.AddTransient<PlaceholderViewModel>();

        // Owns draft state for the visual-effect toggles, so it lives as long as the shell and resets
        // its drafts on load rather than being rebuilt per navigation.
        services.AddSingleton<ThemesViewModel>();

        // Carries the plan between review, applying, and result, so all three observe one instance.
        services.AddSingleton<ChangeSessionViewModel>();
    }

    private static void AddOperations(IServiceCollection services)
    {
        services.AddSingleton<IVisualEffectsAccess, WindowsVisualEffectsAccess>();
        services.AddSingleton<WindowsBuildProbe>();

        // One instance per setting: an operation addresses exactly one documented target.
        foreach (var setting in Enum.GetValues<VisualEffectSetting>())
        {
            var captured = setting;
            services.AddSingleton<IOperation>(provider => new VisualEffectsOperation(
                captured,
                provider.GetRequiredService<IVisualEffectsAccess>()));
        }

        services.AddSingleton<IBackupCaptureProvider>(provider =>
            new OperationBackupCaptureProvider(provider.GetServices<IOperation>()));
    }

    private static void AddPersistence(IServiceCollection services)
    {
        services.AddSingleton(_ => WinoraDataPaths.ForCurrentUser());
        services.AddSingleton(provider => new AtomicJsonFile(provider.GetRequiredService<WinoraDataPaths>()));
        services.AddSingleton<IClock, SystemClock>();

        services.AddSingleton<IBackupRepository>(provider => new BackupRepository(
            provider.GetRequiredService<WinoraDataPaths>(),
            provider.GetRequiredService<IBackupCaptureProvider>()));

        // This process is the medium-integrity app, never the elevated host.
        services.AddSingleton<IDurableOperationJournal>(provider => new DurableOperationJournal(
            provider.GetRequiredService<WinoraDataPaths>(),
            DurableJournalActor.App));

        // GlobalMutationLease proves its owner is the signed Winora package and throws without
        // package identity. That is the intended control, not a bug: an unpackaged process must not
        // be able to squat on the lock that guards system mutation. An unpackaged development launch
        // therefore gets a lease that never grants, and the UI states why applying is unavailable.
        services.AddSingleton<IMutationLease>(provider =>
            provider.GetRequiredService<IDeploymentState>().IsPackaged
                ? new GlobalMutationLease(
                    provider.GetRequiredService<WinoraDataPaths>(),
                    MutationLeasePackageRole.App)
                : new UnavailableMutationLease());

        // Only allowlisted catalog operation ids may appear in the sanitized journal, so the catalog
        // is derived from the registered operations rather than accepting anything.
        services.AddSingleton<IActionJournalOperationCatalog>(provider =>
            new FixedActionJournalOperationCatalog(
                provider.GetServices<IOperation>().Select(static operation => operation.OperationId)));
        services.AddSingleton(provider => new ActionJournal(
            provider.GetRequiredService<WinoraDataPaths>(),
            provider.GetRequiredService<IActionJournalOperationCatalog>()));

        // Must be a singleton: confirmation tokens are single-use and are consumed by the
        // coordinator, so the review screen and the coordinator have to share one authority.
        services.AddSingleton<ConfirmationAuthority>();
        services.AddSingleton<ChangeCoordinator>();
    }
}
