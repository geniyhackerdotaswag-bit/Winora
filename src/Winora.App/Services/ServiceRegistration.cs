using Microsoft.Extensions.DependencyInjection;
using Winora.App.Navigation;
using Winora.App.ViewModels;
using Winora.Core.Appearance;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Infrastructure.Appearance;
using Winora.Infrastructure.Backups;
using Winora.Infrastructure.History;
using Winora.Infrastructure.Journal;
using Winora.Infrastructure.Leases;
using Winora.Infrastructure.Operations;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;
using Winora.Infrastructure.Bypass;
using Winora.Infrastructure.Profile;
using Winora.Infrastructure.Recovery;
using Winora.Infrastructure.Time;
using Winora.System.Backups;
using Winora.System.Operations;
using Winora.System.Updates;
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
        services.AddSingleton<IChangePlanArchive, ChangePlanArchive>();
        services.AddSingleton<IRecoveryState, RecoveryState>();
        services.AddSingleton<IElevationProbe, WindowsElevationProbe>();
        services.AddSingleton<IPackageIdentityAccessor, PackageIdentityAccessor>();
        services.AddSingleton<IElevationRelauncher, ElevationRelauncher>();
        services.AddSingleton<IChangeExecutor, ChangeExecutor>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(provider => provider.GetRequiredService<NavigationService>());

        services.AddSingleton<ShellViewModel>();
        services.AddTransient<PlaceholderViewModel>();

        // Owns draft state for the visual-effect toggles, so it lives as long as the shell and resets
        // its drafts on load rather than being rebuilt per navigation.
        services.AddSingleton<ThemesViewModel>();
        services.AddSingleton<TaskbarViewModel>();
        services.AddSingleton<ExplorerViewModel>();
        services.AddTransient<CleanupViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<StartupViewModel>();

        // Carries the plan between review, applying, and result, so all three observe one instance.
        services.AddSingleton<ChangeSessionViewModel>();
    }

    private static void AddOperations(IServiceCollection services)
    {
        services.AddSingleton<IVisualEffectsAccess, WindowsVisualEffectsAccess>();
        services.AddSingleton<IWindowsBuildProbe, WindowsBuildProbe>();
        services.AddSingleton<ISystemSummaryService, SystemSummaryService>();

        // One instance per setting: an operation addresses exactly one documented target.
        foreach (var setting in Enum.GetValues<VisualEffectSetting>())
        {
            var captured = setting;
            services.AddSingleton<IOperation>(provider => new VisualEffectsOperation(
                captured,
                provider.GetRequiredService<IVisualEffectsAccess>()));
        }

        services.AddSingleton<IRunEntryProbe, WindowsRunEntryProbe>();
        services.AddSingleton<IRunEntryStore, WindowsRunEntryStore>();
        services.AddSingleton<IOperationFactory, RunEntryOperationFactory>();
        services.AddSingleton<IStartupInventoryService, StartupInventoryService>();
        services.AddSingleton<ITempLocationProbe, WindowsTempLocationProbe>();
        services.AddSingleton<ICleanupSurveyService, CleanupSurveyService>();

        services.AddSingleton<ITempCleaner, WindowsTempCleaner>();
        services.AddSingleton<ICursorFolderScanner, CursorFolderScanner>();
        services.AddSingleton<ICursorPreviewRenderer, CursorPreviewRenderer>();
        services.AddSingleton<ISoundPackBuilder, SoundPackBuilder>();
        services.AddSingleton<ISoundFolderScanner, SoundFolderScanner>();
        services.AddSingleton<ISoundSchemeApplier, SoundSchemeApplier>();
        services.AddSingleton<ISoundPlayer, WindowsSoundPlayer>();
        services.AddSingleton<ISoundService, SoundService>();
        services.AddTransient<SoundsViewModel>();
        services.AddSingleton<ICursorApplier, WindowsCursorApplier>();
        services.AddSingleton<ICursorService, CursorService>();
        services.AddTransient<CursorsViewModel>();

        services.AddSingleton<IOperationRollback, OperationRollback>();
        services.AddSingleton<IChangeHistory, ChangeHistory>();
        services.AddSingleton<IChangeHistoryService, ChangeHistoryService>();
        services.AddTransient<ChangesViewModel>();
        services.AddTransient<RecoveryViewModel>();

        services.AddSingleton(provider =>
            new WinoraStateBackupService(provider.GetRequiredService<WinoraDataPaths>()));
        services.AddSingleton<IStateBackupCatalog, StateBackupCatalog>();
        services.AddSingleton<IStateBackupService, StateBackupService>();
        services.AddTransient<BackupsViewModel>();

        services.AddSingleton<IActionJournalWriter, ActionJournalWriter>();
        services.AddSingleton<IActionJournalReader, ActionJournalReader>();
        services.AddTransient<JournalViewModel>();

        services.AddSingleton<IAppEnvironment, AppEnvironment>();
        services.AddTransient<SettingsViewModel>();

        // Winora's own colours. Singletons, because the brush service holds the palette in force
        // and every screen that reads it has to see the same one.
        services.AddSingleton<IHighContrastProbe, HighContrastProbe>();
        services.AddSingleton<IThemeBrushService, ThemeBrushService>();
        services.AddTransient<AppearanceViewModel>();

        // Singletons: the catalog holds the folder every other piece resolves paths against, and the
        // installer keeps the release the user was shown so the install is of exactly that one.
        services.AddSingleton<IBypassStrategyCatalog, BypassStrategyCatalog>();
        services.AddSingleton<IBypassProcessController, BypassProcessController>();
        services.AddSingleton<IBypassReleaseInstaller, BypassReleaseInstaller>();
        services.AddSingleton<IBypassService, BypassService>();
        // Singletons, and for the same reason as the profile store: this is one file, and two
        // readers of it would disagree about what has already been tried.
        services.AddSingleton<IBypassAttemptStore, BypassAttemptStore>();
        services.AddSingleton<IBypassHistory>(provider =>
            new BypassHistoryService(
                provider.GetRequiredService<IBypassAttemptStore>(),
                TimeProvider.System));
        services.AddTransient<BypassViewModel>();

        // Winora's own release feed and updater. Singletons for the same reason as the bypass
        // installer above: the release the person was shown must be the release that gets
        // installed, so the object holding it has to outlive the screen that showed it.
        services.AddSingleton<IAppInstallLocation, AppInstallLocation>();
        services.AddSingleton<IShortcutWriter, StartMenuShortcut>();
        services.AddSingleton<IAppReleaseFeed, AppReleaseFeed>();
        services.AddSingleton<IAppUpdater>(provider =>
            new AppUpdater(provider.GetRequiredService<IAppInstallLocation>()));
        services.AddSingleton<IAppInstaller>(provider =>
            new AppInstaller(
                provider.GetRequiredService<IAppInstallLocation>(),
                provider.GetRequiredService<IShortcutWriter>()));

        // Singletons: the store is a file and the card appears in two places, which must not read
        // it into two different answers.
        services.AddSingleton<IUserProfileStore, UserProfileStore>();

        // The folder the two pictures are copied into, beside the profile that names them.
        services.AddSingleton<IProfileMediaStore, ProfileMediaStore>();
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddTransient<ProfileViewModel>();
        services.AddTransient<RegistrationViewModel>();

        // The presentation-layer wrapper: UpdateViewModel may not reference Winora.System directly
        // (SolutionStructureTests.ViewModels_never_reference_infrastructure_or_system_directly), so
        // this is what stands between it and the feed/updater above.
        services.AddSingleton<IAppUpdateService, AppUpdateService>();
        services.AddSingleton<UpdateViewModel>();

        services.AddSingleton<IPowerSchemeAccess, WindowsPowerSchemeAccess>();
        services.AddSingleton<ISystemLoadProbe, WindowsSystemLoadProbe>();

        // Singleton on purpose: processor load and network throughput are rates, computed from the
        // difference between consecutive samples. A fresh probe per page visit would have nothing
        // to compare against and would report a flat zero.
        services.AddSingleton<ILiveMetricsProbe, LiveMetricsProbe>();
        services.AddSingleton<IHardwareInventoryProbe, WmiHardwareInventoryProbe>();
        services.AddSingleton<IProcessorFactsProbe, WmiProcessorFactsProbe>();
        services.AddSingleton<IPerformanceService, PerformanceService>();
        services.AddTransient<PerformanceViewModel>();

        services.AddSingleton<IUserShellPreferenceAccess, WindowsUserShellPreferenceAccess>();
        services.AddSingleton<IShellPreferenceCatalog, ShellPreferenceCatalog>();
        foreach (var entry in DocumentedShellValues.All)
        {
            var captured = entry;
            services.AddSingleton<IOperation>(provider => new UserShellPreferenceOperation(
                captured,
                provider.GetRequiredService<IUserShellPreferenceAccess>()));
        }

        services.AddSingleton<IWindowsThemeState, WindowsThemeState>();
        services.AddSingleton<IThemeLauncher, ShellThemeLauncher>();
        services.AddSingleton<IOperation>(provider => new WindowsThemeOperation(
            provider.GetRequiredService<IWindowsThemeState>(),
            new WindowsThemeApplier(
                provider.GetRequiredService<IWindowsThemeState>(),
                provider.GetRequiredService<IThemeLauncher>(),
                WindowsThemeState.UserThemesFolder)));

        services.AddSingleton<IWindowsThemeService, WindowsThemeService>();

        // Resolves both the fixed operations above and, through factories, domains whose targets are
        // discovered at runtime. Startup reconciliation runs in a fresh process with only the id
        // from the durable journal, so resolution must not depend on this session's instances.
        services.AddSingleton<IOperationCatalog>(provider => new CompositeOperationCatalog(
            provider.GetServices<IOperation>(),
            provider.GetServices<IOperationFactory>()));

        services.AddSingleton<IBackupCaptureProvider>(provider =>
            new OperationBackupCaptureProvider(provider.GetRequiredService<IOperationCatalog>()));
    }

    private static void AddPersistence(IServiceCollection services)
    {
        services.AddSingleton(_ =>
        {
            // Before anything opens the store. An older build kept it inside the package container,
            // which Windows deletes on uninstall, so a store found there is moved out first. The
            // migration is idempotent and leaves both locations untouched if it cannot finish.
            WinoraStoreMigration.ForCurrentUser().Run();
            return WinoraDataPaths.ForCurrentUser();
        });
        services.AddSingleton(provider => new AtomicJsonFile(provider.GetRequiredService<WinoraDataPaths>()));
        services.AddSingleton<IClock, SystemClock>();

        services.AddSingleton<IColorSchemeStore>(provider => new ColorSchemeStore(
            provider.GetRequiredService<WinoraDataPaths>(),
            provider.GetRequiredService<AtomicJsonFile>()));

        services.AddSingleton<IBackupRepository>(provider => new BackupRepository(
            provider.GetRequiredService<WinoraDataPaths>(),
            provider.GetRequiredService<IBackupCaptureProvider>()));

        // Registered by its concrete type as well, and the interface resolves to the same instance.
        // The history reader needs the concrete journal because reading the whole catalog, rather
        // than only the incomplete operations, is an Infrastructure-internal capability that does
        // not belong on the Core contract. Registering only the interface left that dependency
        // unresolvable and the app failed to start with nothing in any log.
        services.AddSingleton(provider => new DurableOperationJournal(
            provider.GetRequiredService<WinoraDataPaths>(),
            DurableJournalActor.App));

        services.AddSingleton<IDurableOperationJournal>(provider =>
            provider.GetRequiredService<DurableOperationJournal>());

        // One lease for both shapes of the app. It used to be handed out only to the packaged
        // build, with everything else given a lease that never granted — which meant the portable
        // .exe, the only build anybody outside this project can get, could not change a thing.
        //
        // The lease still does its real job: a named mutex keyed to the user, so two Winoras cannot
        // mutate the system at once. What changed is the ticket to hold it. Packaged, that remains
        // the package Windows reports; unpackaged, it is the executable's own path. See
        // WindowsMutationLeaseOwnerIdentity.ValidateRole for what is given up and what is not.
        services.AddSingleton<IMutationLease>(provider =>
            new GlobalMutationLease(
                provider.GetRequiredService<WinoraDataPaths>(),
                MutationLeasePackageRole.App));

        // Only allowlisted catalog operation ids may appear in the sanitized journal, so the catalog
        // is derived from the registered operations rather than accepting anything.
        // The allowlist itself lives in JournalAllowlist so it can be asserted over directly; see
        // that type for why. Reclamation contributes no IOperation and is unioned in there.
        services.AddSingleton<IActionJournalOperationCatalog>(provider =>
            new FixedActionJournalOperationCatalog(
                JournalAllowlist.CatalogOperationIds(
                    provider.GetServices<IOperation>()
                        .Select(static operation => operation.OperationId))));
        services.AddSingleton(provider => new ActionJournal(
            provider.GetRequiredService<WinoraDataPaths>(),
            provider.GetRequiredService<IActionJournalOperationCatalog>()));

        // The interface resolves to that same instance. Registering only the concrete type left
        // every consumer of IActionJournal unresolvable, which is fatal at startup.
        services.AddSingleton<IActionJournal>(provider =>
            provider.GetRequiredService<ActionJournal>());

        // Must be a singleton: confirmation tokens are single-use and are consumed by the
        // coordinator, so the review screen and the coordinator have to share one authority.
        services.AddSingleton<ConfirmationAuthority>();
        services.AddSingleton<ChangeCoordinator>();
    }
}
