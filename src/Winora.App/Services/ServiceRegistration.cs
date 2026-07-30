using Microsoft.Extensions.DependencyInjection;
using Winora.App.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Services;

/// <summary>The single composition root. Nothing else constructs infrastructure or system types.</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddWinora(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(_ => RouteRegistry.Create());
        services.AddSingleton<ILocalizationService, ResourceLocalizationService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(provider => provider.GetRequiredService<NavigationService>());

        // ViewModels are transient unless they own draft state.
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<PlaceholderViewModel>();

        return services;
    }
}

// The operation graph — WinoraDataPaths, AtomicJsonFile, BackupRepository, DurableOperationJournal,
// GlobalMutationLease, ActionJournal, ConfirmationAuthority, ChangeCoordinator, and the documented
// Windows adapters — is registered when the first real screen exercises it. Registering it now
// would wire a graph nothing can run yet, and several of those constructors need collaborators
// (IBackupCaptureProvider, IActionJournalOperationCatalog, DurableJournalActor, an IClock
// implementation) whose composition deserves to be decided against a working screen, not guessed.
