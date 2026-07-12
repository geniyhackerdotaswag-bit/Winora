# Winora MVP — Design Specification

**Date:** 2026-07-12  
**Status:** Approved architecture and UX; written specification ready for review  
**Product:** Winora — safe Windows 11 customization desktop app

## 1. Product goal

Winora is a calm, native Windows 11 desktop application for inspecting and changing supported personalization settings without hiding risk or recovery details. Every mutating operation is planned before it runs, explained in exact terms, backed up when rollback is meaningful, verified afterward, and logged without secrets.

The MVP must feel comparable in discipline and finish to Microsoft PowerToys, Windows Terminal, Dev Home, and Visual Studio Installer. It must not behave like a registry-tweak collection.

### Success criteria

- Users can understand what Winora will change before confirming it.
- Canceling before the apply stage leaves Windows unchanged.
- Supported changes have a verified backup and an idempotent rollback path.
- Unsupported Windows versions or mechanisms are reported explicitly and blocked.
- Administrator elevation occurs only for the specific operation that requires it; the main UI remains non-elevated.
- Every visible control performs an action, opens a route, or explicitly reports that the feature is guided or not yet supported.
- The x64 Debug and Release configurations build successfully and the packaged app launches on Windows 11.

## 2. Fixed technical baseline

The implementation targets the stable tools present on the development machine:

- C# on .NET 10 LTS.
- SDK used for development: installed stable `10.0.203`. `global.json` pins that baseline with `rollForward: latestFeature` and `allowPrerelease: false`; CI/release builds move to the latest serviced .NET 10 SDK after it is installed (Microsoft currently lists SDK 10.0.301/runtime 10.0.9).
- Target framework: `net10.0-windows10.0.26100.0`.
- Minimum supported OS: Windows 11 build 22000 (`SupportedOSPlatformVersion` and `TargetPlatformMinVersion` set to `10.0.22000.0`).
- Windows App SDK: stable `Microsoft.WindowsAppSDK` 2.2.x, initially pinned to `2.2.0`.
- WinUI 3, packaged single-project MSIX application.
- Visual Studio 2026 Community / MSBuild 18 for local build and packaging.
- Primary development architecture: x64. ARM64 packaging is a post-MVP extension; the code must remain architecture-neutral.

The machine has .NET SDK 10.0.203, Visual Studio 2026 18.5.2, MSBuild 18.5, Windows SDK 26100 tools, and Windows App Runtime 2.2 installed. The WinUI `dotnet new` template is not registered, so the solution will be scaffolded manually against the installed Visual Studio template structure.

Stable releases only are allowed. Preview and Experimental Windows App SDK channels are excluded.

## 3. Scope and safety boundary

### MVP capabilities

- Dashboard and compatibility summary.
- Startup entry inspection and supported enable/disable operations.
- Windows theme and transparency status, Winora theme selection, supported visual-effect controls, and guided Windows Settings actions where no documented setter exists.
- System sound scheme inspection and sound preview; guided persistent configuration through the Windows-owned UI.
- Cursor scheme inspection and in-app preview; guided persistent configuration through the Windows-owned UI.
- Folder icon changes through documented `Desktop.ini` behavior.
- Shortcut icon changes through the documented Shell Link API.
- Applied-change history.
- Operation-specific backups and manual Winora-state backup.
- Verified, idempotent rollback for directly applied Winora operations.
- Restore-point creation service for operations classified as high risk.
- Separate sanitized action journal.
- Compatibility and administrator-rights reporting.

### Explicit non-goals for MVP

- No undocumented `Explorer`, `DWM`, `StartupApproved`, `AppEvents`, cursor, or theme registry tweaks.
- No debloat, service disabling, policy bypass, security weakening, shell replacement, taskbar hacks, or patching system binaries.
- No SQLite, cloud synchronization, accounts, telemetry, plugin marketplace, or remote execution.
- No silent elevation and no always-elevated main application.
- No claim that a Winora backup is a full Windows image or a substitute for System Restore.
- No direct persistent Windows theme, transparency, sound-scheme, or cursor-scheme setter unless Microsoft documents a supported API during implementation. In the fixed MVP these actions are guided through Windows Settings or a Windows-owned control panel.

The guided boundary is intentional: a working route that explains support and opens the correct Windows-owned UI is safer than an undocumented mutation disguised as a feature.

## 4. Solution structure

```text
Winora/
├─ Winora.sln
├─ AGENTS.md
├─ README.md
├─ Directory.Build.props
├─ Directory.Packages.props
├─ global.json
├─ docs/
│  ├─ architecture.md
│  ├─ design-system.md
│  ├─ safety-model.md
│  └─ superpowers/specs/2026-07-12-winora-design.md
├─ src/
│  ├─ Winora.Core/
│  ├─ Winora.Infrastructure/
│  ├─ Winora.System/
│  ├─ Winora.ElevatedHost/
│  └─ Winora.App/
└─ tests/
   ├─ Winora.Core.Tests/
   ├─ Winora.Infrastructure.Tests/
   ├─ Winora.System.Tests/
   └─ Winora.App.Tests/
```

### Project responsibilities

#### `Winora.Core`

- Pure .NET domain records, enums, policies, operation contracts, repository interfaces, and the change-session coordinator.
- Depends on no WinUI, Windows App SDK, JSON serializer, registry, PowerShell, COM, P/Invoke, filesystem implementation, or concrete Windows API.
- Contains no dialogs, navigation, or presentation strings tied to controls.

#### `Winora.Infrastructure`

- Implements Core persistence contracts with `System.Text.Json`.
- Owns atomic JSON writes, immutable per-event journal files, schema versions, hashing, local paths, migrations, and redaction.
- Receives its base data directory through dependency injection.
- Does not know about WinUI and does not perform system customization.

#### `Winora.System`

- Implements Core operation and capability interfaces with documented Windows APIs.
- Encapsulates registry access, Shell APIs, COM, `SRSetRestorePointW`, `SystemParametersInfo`, process launching, filesystem attributes, and OS-version checks behind narrow adapters.
- Never displays a dialog, notification, or window.
- Never asks the user to confirm. It only returns structured plans, progress, results, errors, and recovery options.
- Does not persist ViewModel state.

#### `Winora.ElevatedHost`

- A minimal non-UI executable with a `requireAdministrator` manifest.
- Accepts only whitelisted operation identifiers and a one-time change-session token.
- Validates the plan digest, nonce, target paths, allowed value types, and operation capability before execution.
- Communicates structured progress/results to the non-elevated app through a one-time named pipe.
- Cannot execute arbitrary commands or arbitrary PowerShell supplied by the UI.

#### `Winora.App`

- WinUI 3 views, ViewModels, navigation, dialogs, confirmation pages, notifications, application theme, window lifecycle, and DI composition root.
- Owns every user-facing confirmation, error, InfoBar, TeachingTip, progress surface, and recovery choice.
- ViewModels depend only on Core/application-facing interfaces. They never access the registry, PowerShell, system files, COM, P/Invoke, or Windows APIs directly.

### Dependency rule

```text
Winora.App ────────────────┐
  │                        │
  ├──> Winora.Core <───────┼── Winora.Infrastructure
  │                        │
  └──> Winora.System ──────┘

Winora.ElevatedHost ──> Winora.Core + Winora.System
```

No dependency may point from Core toward an outer layer.

## 5. Core domain model

### Change plan

Every direct mutation produces an immutable `ChangePlan` containing:

- `PlanId`, `OperationId`, category, title, and plain-language summary.
- Exact current and proposed values using display-safe value objects.
- Ordered `ChangeStep` records describing every target and action.
- `RiskLevel`: `Informational`, `Low`, `Medium`, or `High`.
- `PrivilegeRequirement`: `StandardUser` or `Administrator`.
- `RollbackCapability`: `Full`, `Partial`, `NotAvailable`, or `NotApplicable`.
- `RestartRequirement`: `None`, `Explorer`, `SignOut`, or `Reboot`.
- `SupportStatus`: `Supported`, `SupportedWithElevation`, `Guided`, `Unsupported`, or `Unknown`.
- A source-state fingerprint used to detect drift between preview, confirmation, apply, and rollback.
- The Microsoft documentation URI that authorizes the direct mechanism, when the plan mutates Windows.
- Backup requirements and expected verification probes.
- Whether a System Restore point is mandatory.
- A deterministic SHA-256 digest over the canonical plan content.

`Unknown`, `Unsupported`, `Partial` rollback, or `NotAvailable` rollback blocks direct apply in the MVP. `NotApplicable` is allowed only for a non-destructive safety artifact, such as creating a restore point.

### Operation capability

Every system operation implements a capability probe that reports:

- Current Windows edition, version, and build support.
- Required API/type/method availability.
- Required privileges.
- Whether the target is writable and supported.
- Whether backup, verification, and rollback are available.
- A user-facing reason when the operation is guided or blocked.

Capability probing is read-only.

### Change-session states

```text
Draft
  → Preflight
  → DryRunReady
  → AwaitingConfirmation
  → Confirmed
  → RevalidatingSource
  → BackupCreating
  → BackupVerified
  → BeginApply
  → RestorePointCreating (only when required)
  → Applying
  → AppliedPendingVerification
  → Succeeded
```

Terminal and recovery states:

- `CanceledNoChanges` — canceled before `BeginApply`; Windows is untouched.
- `Unsupported` — capability probe blocked the plan.
- `PlanInvalidatedNoChanges` — source fingerprint changed after dry-run; the user must review a newly generated diff.
- `BackupFailedNoChanges` — backup creation or readback verification failed.
- `RestorePointFailedNoChanges` — mandatory restore point failed; apply never started.
- `FailedNoChanges` — apply failed before any step committed.
- `PartiallyAppliedRecoveryRequired` — one or more steps committed and a later step failed or cancellation was requested between steps.
- `VerificationFailedRollbackOffered` — apply finished but verification did not match the plan.
- `RollingBack`, `RolledBack`, `AlreadyRestored`, and `RollbackFailedRecoveryRequired`.

Only one mutating change session may run at a time. Other plans remain drafts.

## 6. Dry-run, confirmation, apply, and rollback

### Apply flow

1. A settings page edits a local draft only.
2. `Preview change` requests a read-only capability probe and dry-run plan.
3. Winora opens a dedicated review page, not a cramped modal.
4. The review page shows exact targets, before/after values, risk, rights, rollback coverage, backup action, restore-point requirement, and restart/sign-out requirement.
5. Cancel returns to the draft and performs no write, elevation, backup, restore point, Explorer restart, or system broadcast.
6. Confirm creates a confirmation token bound to the plan digest.
7. The coordinator re-reads the affected state and compares its fingerprint with the dry-run. Drift invalidates the plan and generates a new review; no write occurs.
8. The coordinator creates an operation-specific backup in a staging directory.
9. Winora reads the staged backup, validates its schema and SHA-256 hashes, and atomically publishes it.
10. The user may cancel through backup verification. `BeginApply` is the explicit point after which the UI no longer offers ordinary cancellation.
11. If the plan is high risk, the elevated host creates and verifies a genuinely new System Restore point as the first authorized apply step. If Windows reuses an older point or creation cannot be verified, the operation is blocked before its target mutation.
12. The operation applies ordered steps. Progress reports identify the active step without exposing secrets.
13. Verification reads the resulting Windows state independently and compares it with the plan.
14. Success commits the applied-change record and a sanitized journal event.
15. Verification failure opens an error result with a primary `Restore automatically` action. Rollback is offered, not executed silently.

### Partial execution

- Ordinary cancellation is unavailable after `BeginApply`. An emergency stop request is honored only between steps and necessarily produces `PartiallyAppliedRecoveryRequired`.
- An indivisible step is never interrupted halfway.
- The recovery screen lists completed, failed, and untouched steps and offers rollback of completed steps in reverse order.
- Closing the app does not erase the recovery record. On next launch, Dashboard and Changes show a persistent recovery InfoBar.

### Rollback flow

1. User opens an applied or recovery-required change.
2. Winora probes current support, reads/verifies the linked backup, and compares the current fingerprint with the applied and backed-up fingerprints.
3. Unexpected external drift blocks automatic overwrite and opens a conflict review instead of guessing.
4. A rollback dry-run lists the exact values/files that will be restored.
5. User confirms; elevation is requested only if the rollback requires it.
6. Winora creates one bounded pre-rollback recovery checkpoint of the current state. Recovery checkpoints do not recursively create more checkpoints.
7. Steps run in reverse order and are independently verified.
8. If the current state already equals the backup, the step returns `AlreadyRestored` and is successful.
9. Repeating rollback is safe and produces the same final state.

## 7. Persistence and file layout

The default root is `%LOCALAPPDATA%\Winora`. The path is provided to Infrastructure by the App composition root.

```text
%LOCALAPPDATA%\Winora\
├─ Data\
│  ├─ app-settings.json
│  ├─ change-index.json
│  └─ recovery-index.json
├─ Backups\
│  └─ {backup-id}\
│     ├─ manifest.json
│     └─ payload\...
├─ Journal\
│  ├─ index.json
│  └─ Events\{event-id}.json
├─ Assets\
│  └─ managed copies of user-selected .ico/.wav/.cur/.ani files
└─ Pending\
   └─ short-lived elevated-operation envelopes
```

### Atomic JSON writing

- Serialize into a uniquely named temporary file in the destination directory.
- Flush managed and OS buffers.
- Read the temporary file back, validate schema, and compute hashes.
- Replace an existing target with `File.Replace`; publish a new target with a same-volume atomic move.
- Retain the last-known-good replacement file until the new target passes readback.
- Use a process-wide async lock so two writes cannot interleave.
- Backup directories are built under `{id}.staging`, fully verified, then renamed to `{id}` on the same volume.

All persisted documents have `schemaVersion`, `createdUtc`, and a stable identifier. Infrastructure owns DTOs and migrations; Core models do not contain JSON attributes.

### Action journal

- Stored separately as one immutable, atomically published JSON document per event; `index.json` is a rebuildable cache.
- Each event uses the same temp → flush → readback → atomic-move protocol as other JSON documents.
- Contains timestamp, operation identifier, category, status, risk, privilege class, support status, and correlation ID.
- Never stores tokens, environment variables, command lines, registry data dumps, full user paths, file contents, usernames, or raw exception payloads.
- User-visible labels replace paths; a salted local hash may correlate the same target without revealing it.

Backups may contain the minimum exact paths and values needed for rollback. They are not copied into the journal and inherit user-only filesystem ACLs.

### Retention defaults

- Never delete a backup linked to an active or recovery-required change.
- Keep at least the newest 50 verified operation backups; older backups become eligible only after 90 days.
- Keep journal events for 365 days, capped at 25,000 events. Recovery, failure, and rollback-failure events are retained while their linked change exists.
- Retention runs only after a successful atomic state write and never during apply/rollback.

## 8. Documented operation catalog

| Area | MVP behavior | Rights | Rollback | Restart | Support rule |
|---|---|---:|---:|---:|---|
| Winora light/dark theme | Direct app setting through WinUI `RequestedTheme` | Standard | Full | None | Supported |
| Windows light/dark mode | Show current state when safely detectable; open `ms-settings:colors` for the change | Standard | N/A | None | Guided; no undocumented setter |
| Windows transparency | Show status and open `ms-settings:colors` | Standard | N/A | None | Guided; no undocumented setter |
| UI effects and client-area animations | Direct documented `SystemParametersInfo` operations with broadcast | Standard | Full | None | Supported when the SPI action is present |
| Startup — `HKCU/HKLM ...\Run` | Inspect documented Run entries; disable by backed-up value removal; enable by exact restore | HKCU standard; HKLM admin | Full | Sign-in affects launch | Supported/with elevation |
| Startup folders | Inspect user/common Startup known folders; move a shortcut to/from a Winora-managed disabled directory | User standard; common folder admin | Full | Sign-in affects launch | Supported/with elevation |
| Packaged startup tasks, services, scheduled tasks, `StartupApproved` | Read-only source badge or unsupported message | — | — | — | Guided/unsupported in MVP |
| System sounds | Inspect known scheme display data and preview user-selected WAV files; open Windows Sound control panel for persistent changes | Standard | N/A | None | Guided for persistent change |
| Cursor schemes | Inspect display data and render an in-app preview; open Mouse Settings for persistent changes | Standard | N/A | None | Guided for persistent change |
| Folder icon | Documented Unicode `Desktop.ini`, folder attribute handling, managed `.ico` copy, and shell change notification | Usually standard | Full | None; Explorer refresh only | Supported on writable filesystem folders |
| Shortcut icon | Documented `IShellLink::SetIconLocation` + `IPersistFile::Save` | Usually standard | Full | None; Explorer refresh only | Supported for writable `.lnk` files |
| Restore point | Documented `SRSetRestorePointW` sequence through elevated host | Admin | Not applicable | None | Supported only when System Restore is enabled and a new sequence can be verified |

### Registry policy

Registry access is allowed only when a Microsoft document identifies the key as a supported integration point, such as Run/RunOnce. Each implementation source file includes a link to its supporting Microsoft documentation. If that documentation cannot be cited, the operation is not implemented as a direct mutation.

### PowerShell policy

No PowerShell is required for the fixed MVP operation catalog. If a later operation genuinely needs it, it must use a versioned script owned by Winora, fixed parameters, strict path validation, no interpolated command strings, and redacted output. ViewModels can never invoke it.

## 9. Administrator and elevation model

- The Winora App process starts and remains a standard-user process.
- `IPrivilegeService` evaluates the confirmed plan, not the current page.
- Standard-user plans never trigger UAC.
- Administrator plans are confirmed in the UI before UAC appears.
- The elevated helper receives only a signed envelope containing a whitelisted operation ID, plan digest, nonce, expiration, and validated payload.
- The helper re-runs support and path validation after elevation.
- A timed one-use named pipe returns progress and the final structured result.
- Elevation cancellation becomes `CanceledNoChanges` if apply never started.
- Winora never changes `SystemRestorePointCreationFrequency` or enables System Protection through a hidden policy mutation.

## 10. Navigation and screens

### `NavigationView`

```text
Главная

ПЕРСОНАЛИЗАЦИЯ
  Темы и эффекты
  Звуки
  Курсоры
  Иконки

СИСТЕМА
  Автозагрузка
  Изменения
  Резервные копии

Footer
  Журнал действий
  Настройки
```

The pane is 240 px expanded and uses the native compact state when collapsed. Every item has a Microsoft Fluent System Icons Regular icon in the shared 20 px presenter.

### Dashboard

- Page title, concise safety statement, CommandBar (`Как это работает`, `Обновить`, `Создать резервную копию`).
- Windows compatibility InfoBar with a working details route.
- Protection card: last verified backup, active change count, and rollback coverage.
- Windows card: edition/build, privilege mode, support level, and last probe time.
- Four fully clickable quick-access cards: Themes & Effects, Startup, Sounds, and Cursors.
- Fully clickable recent-action rows and a working journal route.
- No decorative control without behavior.
- The real app uses the native AppWindow caption controls; browser-prototype messages for minimize/maximize/close are not carried into WinUI.

### Themes & Effects

- Windows theme/transparency status with `Guided` badges and an `Open Windows Colors` action.
- Winora theme selection: System, Light, Dark.
- Supported visual-effect toggles create local drafts and require dry-run review before apply.
- Unsupported effects explain why rather than exposing a disabled mystery toggle.

### Sounds

- Current scheme summary where detectable without undocumented state mutation.
- Sound-event list with search and WAV preview.
- File validation for extension, readability, local copy, and size.
- Persistent configuration action opens the Windows-owned Sound control panel and explicitly reports `Guided` support.

### Cursors

- Current scheme summary and role list.
- In-app preview for `.cur` and `.ani` assets without changing Windows.
- File validation and managed asset copy.
- Persistent configuration action opens Mouse Settings and reports `Guided` support.

### Icons

- Folder and Shortcut pivot/tabs.
- File/folder picker, current icon preview, proposed icon preview, exact path summary, and support result.
- `Preview change` opens the common dry-run review route.
- Protected, read-only, remote, or unsupported shell targets are blocked with a precise reason.

### Startup

- Searchable list grouped by source: Run key, Startup folder, and unsupported/read-only sources.
- Each row shows name, source, command display (redacted path), state, rights badge, and support badge.
- Toggling changes only the draft. Apply occurs through the common review flow.
- HKLM/common-folder entries show elevation before review confirmation.

### Changes

- Filterable list of planned, successful, partially applied, failed, rolled-back, and recovery-required sessions.
- Detail page shows the immutable plan, completed steps, verification, backup, and journal correlation.
- Rollback action exists only when the verified backup and current capability allow it.

### Backups

- Manual Winora-state backup command.
- Operation backup list with verification status, Windows build, size, and linked change.
- Verify, inspect manifest, and restore actions.
- System Restore point command is a separate elevated safety action and never presented as a Winora JSON backup.

### Journal

- Fully clickable rows, filters, detail drawer/page, and a sanitized JSONL export generated on demand from immutable event documents.
- No secrets or full sensitive paths.

### Settings

- Winora theme, motion preference, backup retention, backup path display, log retention, and diagnostics export.
- A developer dry-run mode may be enabled only in Debug builds.

### Route-only screens

- Compatibility details.
- Change review and rollback review.
- Applying progress.
- Success, failure, and partial-recovery results.
- Applied-change and rollback details.

## 11. Interaction contract

Every visible interactive element must satisfy one of these contracts:

1. Execute an in-memory UI action.
2. Navigate to a real page.
3. Produce a dry-run plan.
4. Invoke a confirmed supported operation.
5. Open an official Windows-owned settings surface.
6. Show an explicit `Guided`, `Unsupported`, or `In development` result.

There are no silent clicks, inert cards, fake chevrons, or unlabeled disabled controls. Disabled controls use `AutomationProperties.HelpText` and nearby explanatory text.

## 12. Design system

### Native foundations

- WinUI 3 controls and theme resources first; custom templates only where the approved Dashboard requires them.
- `Window.SystemBackdrop` uses Mica. Acrylic is limited to transient flyouts or overlays where depth requires it.
- NavigationView, CommandBar, InfoBar, TeachingTip, ContentDialog only for short messages, Card surfaces, ToggleSwitch, Slider, ListView, and ProgressRing.
- System accent is respected for focus/accessibility. Winora green is reserved for the single primary action in a region and positive safety status.

### Tokens

- Spacing: 4, 8, 12, 16, 24, 32, 40, and 48 px only.
- Control radius: 4 px.
- Card radius: 8 px.
- Large/flyout radius: 12 px when required.
- Standard icon geometry: 20 × 20 px.
- Icon containers: 32 × 32 px for cards and 36 × 36 px for feature summaries.
- Expanded navigation width: 240 px.
- Minimum window: 1040 × 720; content scrolls without clipping actions.

### Icons

- One catalog derived from official Microsoft Fluent System Icons Regular 20 assets.
- Selected SVG assets are vendored with their license and exposed through one `FluentIcon` catalog and `IconPresenter` control.
- No emoji, text arrows, mixed MDL2 glyphs, handcrafted SVGs, or per-page icon sizing.
- The presenter optically centers every icon and includes automated geometry checks.

### Typography and density

- `Segoe UI Variable`, falling back to `Segoe UI`.
- Large page title with short supporting copy; body text is constrained to readable line lengths.
- Cards prioritize one statement and two to four meaningful facts.
- No neon glow, saturated decorative gradients, excessive shadows, or crowded KPI grids.

### States and motion

- Hover, Pressed, FocusVisible, Selected, Disabled, Loading, Success, Warning, Error, and RecoveryRequired are defined for interactive surfaces.
- Motion uses native theme transitions, 160–220 ms durations, and natural Fluent easing.
- `UISettings.AnimationsEnabled` and reduced-motion preferences are respected.
- Keyboard focus is never communicated by color alone.

### Accessibility

- WCAG AA contrast for text and actionable boundaries.
- Complete keyboard navigation and visible focus.
- Automation names/help text for every icon-only control.
- High Contrast, 200% scaling, Narrator labels, and minimum hit targets are release checks.
- Each screen receives an internal three-part design review: problems found, fixes made, and elements intentionally unchanged with reasons. After the user's instruction to proceed autonomously, these reviews remain quality gates but do not pause for manual approval unless they reveal a P0/P1 issue or require a material redesign.

## 13. MVVM and dependency injection

- `CommunityToolkit.Mvvm` supplies observable properties and async relay commands.
- `Microsoft.Extensions.DependencyInjection`, Options, and Logging provide composition.
- ViewModels are transient unless they explicitly own draft state.
- Core coordinators and repositories are singletons where they own locks or session state.
- System operation implementations are registered by stable operation ID.
- Navigation resolves Pages and ViewModels through a page factory; ViewModels do not know concrete Page types.
- App-only services: `INavigationService`, `IDialogService`, `INotificationService`, `IWindowService`, and `IFilePickerService`.
- Core-facing UI services never leak WinUI types into Core.
- Russian is the initial UI locale, but all user-facing strings live in `.resw` resources so English can be added without rewriting ViewModels.

## 14. Error handling and recovery UX

- Core/System return typed failures with stable codes, user-safe summaries, diagnostic correlation IDs, and recovery capability.
- Raw exceptions are captured only in a redacted local diagnostic sink.
- Expected conditions use InfoBar, inline validation, or capability badges; unexpected failures use a result page with a copyable correlation ID.
- Backup, restore-point, apply, verification, and rollback failures are separate stages and never collapsed into a generic error.
- App startup checks for incomplete sessions before loading Dashboard.

## 15. Testing strategy

### Core tests

- Change-plan digest stability.
- Source-fingerprint drift invalidates a previously confirmed plan.
- Every state-machine transition, including cancellation before apply.
- Policy blocks unsupported, unknown, partial/no-rollback, and high-risk-without-restore-point plans.
- Partial-operation recovery ordering.
- Idempotent rollback and `AlreadyRestored` behavior.

### Infrastructure tests

- Atomic create and replace.
- Interrupted staging write never replaces the last-known-good file.
- Readback/schema/hash verification failures.
- Concurrent-write serialization.
- Backup-directory publish and corruption detection.
- Atomic per-event journal publication, index rebuild, retention, and redaction rules.
- Schema migration from every supported version.

### System tests

- Unit tests use narrow fake Windows adapters, not mocked ViewModels.
- Capability matrices for supported/unsupported OS builds and privileges.
- Canonical path, extension, writable-target, and protected-target validation.
- Folder `Desktop.ini` and shortcut Shell Link round-trip against temporary test assets.
- Run-key operations use a dedicated Winora test location by default; opt-in Windows integration tests may create and clean a uniquely named HKCU Run value.
- No default test changes production user settings.

### App tests

- Every NavigationView item, Dashboard card, CommandBar item, activity row, confirmation action, error action, and placeholder has a bound command or route.
- ViewModel tests prove draft-only behavior before confirmation.
- Confirmation and rollback review copy contains risk, rights, rollback, and restart facts.
- Navigation contract tests fail when a route key is unregistered.
- A XAML interaction audit flags Button/Card controls without behavior or explicit disabled help text.

### Build and manual release checks

- `dotnet test` for non-UI projects.
- MSBuild 18 `/restore` x64 Debug and Release.
- Packaged app deployment and launch on the local Windows 11 25H2 machine.
- Primary navigation and dry-run/rollback smoke flow.
- Browser prototype comparison for the Dashboard visual baseline.
- No warning/error entries in the app diagnostic log during launch and navigation.
- Accessibility check for keyboard, Narrator labels, High Contrast, 200% scaling, and reduced motion.

## 16. Definition of done for the working MVP

- Solution structure, `AGENTS.md`, `README.md`, architecture, safety, and design-system documentation exist.
- The app builds with the fixed .NET 10 / Windows App SDK 2.2 baseline.
- Mica shell and approved NavigationView/Dashboard are implemented in WinUI 3.
- Every required page is implemented; guided operations are clearly labeled and open the appropriate official Windows surface.
- Direct operations use the shared dry-run, confirmation, verified backup, apply, verify, journal, and rollback pipeline.
- Administrator operations use the isolated elevated helper.
- A failed verification offers automatic rollback and retains a recovery record if rollback fails.
- External drift before apply or rollback never gets overwritten silently.
- Rollback tests prove idempotency.
- JSON state uses atomic writes; the journal is separate and sanitized.
- No undocumented registry mutation appears in the source.
- The interaction audit reports zero dead controls.
- Tests pass, Debug/Release builds succeed, and the packaged app launches.

## 17. Authoritative references

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [Get started with WinUI](https://learn.microsoft.com/en-us/windows/apps/get-started/winui-get-started-overview)
- [Windows versions and SDK overview](https://learn.microsoft.com/en-us/windows/apps/get-started/versioning-overview)
- [Windows App SDK release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)
- [Common Windows settings: personalization colors](https://learn.microsoft.com/en-us/windows/apps/develop/settings/settings-common#personalization---colors)
- [SystemParametersInfoW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow)
- [Run and RunOnce registry keys](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)
- [Theme File Format](https://learn.microsoft.com/en-us/windows/win32/controls/themesfileformat-overview)
- [How to customize folders with Desktop.ini](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-customize-folders-with-desktop-ini)
- [IShellLink::SetIconLocation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishelllinkw-seticonlocation)
- [System Restore API](https://learn.microsoft.com/en-us/windows/win32/sr/system-restore-api)
- [Calling SRSetRestorePoint](https://learn.microsoft.com/en-us/windows/win32/sr/calling-srsetrestorepoint)
