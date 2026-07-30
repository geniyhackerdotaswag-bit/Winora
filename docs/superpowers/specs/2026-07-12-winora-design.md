# Winora MVP — Design Specification

**Date:** 2026-07-12  
**Status:** Architecture and UX baseline retained; safety revision pending user approval
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
- Windows App SDK: stable `Microsoft.WindowsAppSDK` 2.2.x, pinned to `2.2.0`. Two deployment facts constrain this pin and were verified on 2026-07-30. Unpackaged launches resolve the framework through a Dynamic Dependency Lifetime Manager package for their exact version, and only `DDLM.2.2.0.0` is present on the development machine, so a 2.3.x pin cannot start unpackaged at all. Packaged launches ignore the manifest `MinVersion` within the `Microsoft.WindowsAppRuntime.2` family and bind the highest installed version, currently 2.3.1. Raising the pin therefore requires the matching DDLM, and neither 2.2.0 nor 2.3.1 has yet produced a working packaged activation on this machine — see the open packaging issue below.

**Resolved — packaged activation.** A packaged launch used to create a process that exited within a second with no window, no Windows Error Reporting entry, and no managed exception. The cause was the Windows App SDK DeploymentManager auto-initializer: it runs from a static constructor and activates a WinRT type before the runtime is reachable, throwing `REGDB_E_CLASSNOTREG`. A throw inside a `.cctor` terminates the process before any handler can record it, which is why every diagnostic surface was empty. `Winora.App` sets `WindowsAppSdkDeploymentManagerInitialize` to `false`: a packaged app declares a framework dependency, so the OS guarantees the runtime and that initializer has nothing to do. Keep it disabled.

The symptom is worth remembering in general form: when a process dies instantly with no artefact anywhere, suspect a throwing static constructor or module initializer, and get the exception by running the same build as a plain console process where the runtime prints the unhandled exception to stderr.
- WinUI 3, packaged single-project MSIX application. `Winora.App` owns `Package.appxmanifest`; the same signed package contains `Winora.ElevatedHost.exe` as a separate executable.
- Visual Studio 2026 Community / MSBuild 18 for local build and packaging.
- Primary development architecture: x64. ARM64 packaging is a post-MVP extension; the code must remain architecture-neutral.

The machine has .NET SDK 10.0.203, Visual Studio 2026 18.5.2, MSBuild 18.5, Windows SDK 26100 tools, and Windows App Runtime 2.2 installed. The WinUI `dotnet new` template is not registered, so the solution will be scaffolded manually against the installed Visual Studio template structure.

Stable releases only are allowed. Preview and Experimental Windows App SDK channels are excluded.

The fixed MVP distribution is a signed, per-user MSIX/MSIXBundle delivered by App Installer from the Winora release site or by an enterprise channel such as Intune/Configuration Manager. The MVP is not submitted to Microsoft Store because runtime elevation requires the restricted `allowElevation` capability and Store acceptance requires prior Microsoft approval under strict criteria. A later Store build may be offered only after that approval; removing elevation to pass certification is not allowed to silently remove administrative functionality.

`Package.appxmanifest` declares `rescap:Capability Name="runFullTrust"` for the medium-integrity WinUI desktop process and `rescap:Capability Name="allowElevation"` for the packaged helper, using the restricted-capabilities namespace. The helper has its own embedded Win32 application manifest with `requestedExecutionLevel level="requireAdministrator"`. The package is signed in CI with a trusted code-signing certificate whose subject matches the package `Publisher`; the private key is held outside the repository and release signatures are timestamped. Local development may use a development certificate trusted only on the developer machine; no private key is committed.

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
- Documented visual-effect and interface-responsiveness preferences exposed as one operation per `SystemParametersInfo` action pair.
- Documented per-user taskbar and Start values from the Windows 11 settings reference, degrading to guided where the live value kind contradicts the documented semantics.
- Temporary-file reclamation for user-owned locations, performed as a reversible move into a Winora-owned quarantine.
- Retention of Winora's own journals, backups, and quarantine as an explicit, separately confirmed decision.

### Explicit non-goals for MVP

- No undocumented `Explorer`, `DWM`, `StartupApproved`, `AppEvents`, cursor, or theme registry tweaks. Documented `Explorer\Advanced` values named in the Windows 11 settings reference are in scope; `TaskbarSi`, `StuckRects3`, `Taskband\FavoritesMigration`, `UserPreferencesMask`, `VisualFXSetting`, and `Win32PrioritySeparation` are not, because Microsoft either documents them as opaque, calls them undocumented, or has disabled them.
- No debloat, service disabling, policy bypass, security weakening, shell replacement, or patching system binaries. Winora also never terminates `explorer.exe`: restarting the shell is not a documented mechanism, so a pending taskbar change is reported through `RestartRequirement` instead.
- No operation deletes user bytes in the step the user just confirmed. Reclamation is always a reversible move first; freeing the bytes is a separate, separately confirmed retention decision.
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
   ├─ Winora.App.Tests/
   ├─ Winora.Architecture.Tests/        # enforces the dependency rules from csproj XML
   ├─ Winora.Infrastructure.ProcessHost/ # helper exe for cross-process persistence tests
   └─ Winora.Lease.ProcessHost/          # helper exe for cross-process lease-fencing tests
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

- A distinct minimal non-UI executable packaged beside `Winora.App`, with an embedded `requireAdministrator` manifest. It contains no WinUI references, application pages, dialogs, notifications, navigation, or product UI.
- Is launched only by the confirmed operation coordinator through `ShellExecuteEx` with the `runas` verb. Command-line arguments contain only the IPC protocol version, correlation ID, and one-time pipe locator; plans, paths, values, keys, and secrets are never placed on the command line.
- Accepts only versioned request envelopes containing a stable allowlisted operation identifier, canonical plan digest, expected source fingerprint, mutation-lease ID, nonce, expiry, and a schema-validated payload defined by that operation. Unknown protocol majors, operation IDs, fields, value types, or target classes fail closed.
- Validates package identity, publisher, caller process identity, user SID, logon session, plan digest, nonce, target paths, allowed value types, capability, and current source fingerprint after elevation and immediately before each privileged step.
- Communicates typed progress, durable transition acknowledgements, verification data, restore-point sequence data, and a final structured result through a single-use local named pipe. It never returns raw exception payloads, secrets, or unredacted paths.
- Cannot execute arbitrary commands, scripts, dynamic assemblies, shell strings, or PowerShell supplied by the UI. It exposes no general-purpose registry or filesystem command endpoint.
- Uses Infrastructure only through the narrow Core durable-operation-journal contract so a privileged step and System Restore lifecycle can leave crash-recovery evidence. It cannot write ViewModel state, settings, the sanitized action journal, or arbitrary JSON paths.

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

Winora.ElevatedHost ──> Winora.Core + Winora.System + Winora.Infrastructure
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

### Change-session and durable-operation states

`Draft`, `Preflight`, `DryRunReady`, `AwaitingConfirmation`, and `Confirmed` are UI/session states and do not authorize a system mutation. After confirmation, every direct operation creates a durable operation journal before the first possible system mutation. The normative success path for an operation that mutates a target is:

```text
Planned
  → Prepared
  → BackupCreated
  → Applying
  → Applied
  → Verified
  → Completed
```

- `Planned` is persisted after confirmation and contains the immutable plan digest, expected source fingerprint, ordered step IDs, privilege/risk facts, and rollback policy.
- `Prepared` is persisted only after support, caller, target, path, and source-fingerprint validation immediately before backup.
- `BackupCreated` is persisted only after the staged backup has been read back, schema/hash verified, atomically published, and bound to the plan digest and exact pre-backup source fingerprint.
- Before each system action, that step's `Applying` transition is atomically persisted and flushed. After the indivisible mutation returns, `Applied` is persisted before any later system action is authorized. Independent readback then persists `Verified`.
- `Completed` is persisted only after every required step is `Verified`, any System Restore sequence is finalized, and the sanitized action-journal event and applied-change projection can be rebuilt from durable records.
- A privileged step follows the same transitions. `Winora.ElevatedHost` writes its transition through the narrow durable-journal contract before returning the step result, so the UI process is not the sole source of recovery truth.

Every transition is monotonic, names its expected previous revision, and is an immutable atomically published event. A transition write, OS-buffer flush, readback, and hash verification must finish before the next system action starts. A crash between the mutation and its `Applied` event leaves the step durably in `Applying`, which is treated as an uncertain outcome and reconciled by probing Windows; the operation is never replayed blindly.

The explicit manual restore-point command is a non-destructive safety artifact with `backupRequirement: NotApplicable`. Its journal follows `Planned → Prepared → RestorePointBeginRequested → ... → RestorePointEnded → Verified → Completed` and intentionally has no `BackupCreated` transition. This is the only MVP exemption from the target-mutation success path; it cannot be used to bypass backup for an operation that changes another target.

Terminal, error, and recovery states are separate persisted transitions:

- `CanceledNoChanges` and `ElevationCanceledNoChanges` — canceled before any target mutation; Windows is untouched.
- `Unsupported` — capability probe blocked the plan.
- `PlanInvalidatedNoChanges` — a required fingerprint check changed after dry-run; the user must review a newly generated diff.
- `BackupFailedNoChanges` — backup creation or readback verification failed.
- `RestorePointFailedNoChanges` — mandatory restore-point begin or validation failed; target apply never started.
- `ApplyFailedNoChanges` — apply failed before any target step committed.
- `ApplyStepNotApplied` — startup reconciliation proved that the uncertain current step did not commit; the operation is `ApplyFailedNoChanges` only if no earlier step committed, otherwise it requires partial recovery.
- `PartiallyAppliedRecoveryRequired` — one or more steps may have committed and a later step failed, the process crashed, or an emergency stop was requested between steps.
- `VerificationFailedRollbackOffered` — apply finished but verification did not match the plan.
- `RecoveryConflictExternalDrift` — recovery discovered a value that matches neither the confirmed plan nor the verified backup and will not overwrite it.
- `RollbackPlanned`, `RollbackPrepared`, `RollbackCheckpointCreated`, `RollingBack`, `RollbackApplied`, `RollbackVerified`, `RolledBack`, `AlreadyRestored`, and `RollbackFailedRecoveryRequired`.
- `RestorePointRecoveryRequired`, `RestorePointFinalizeFailedRecoveryRequired`, and `RestorePointFinalizeOutcomeUnknown` — a System Restore sequence must be reconciled or finalized before another high-risk operation may run; an unknown finalization outcome is never retried blindly.

Only one mutating change session may run per Windows user across all logon sessions at a time. The WinUI process is single-instanced per user/logon session with Windows App SDK `AppInstance` activation redirection, and apply/rollback additionally requires a global per-user interprocess mutation lease held for the full operation. Other activations are redirected to the primary window; another process that cannot join or acquire the lease may inspect state but must report `OperationBusy` and cannot apply or roll back.

## 6. Dry-run, confirmation, apply, and rollback

### Apply flow

1. A settings page edits a local draft only.
2. `Preview change` requests a read-only capability probe and dry-run plan.
3. Winora opens a dedicated review page, not a cramped modal.
4. The review page shows exact targets, before/after values, risk, rights, rollback coverage, backup action, restore-point requirement, and restart/sign-out requirement.
5. Cancel returns to the draft and performs no write, elevation, backup, restore point, Explorer restart, or system broadcast.
6. Confirm creates a confirmation token bound to the plan digest.
7. The coordinator acquires the per-user interprocess mutation lease, creates the durable operation journal in `Planned`, and re-reads the affected state. Drift invalidates the plan and generates a new review; no system write occurs.
8. Immediately before backup, the coordinator probes the source again. Only an exact match may transition to `Prepared`; otherwise it records `PlanInvalidatedNoChanges`, releases the lease, and requires a new dry-run.
9. The coordinator creates an operation-specific backup in a staging directory from the actual captured registry value bytes/file handles, derives `capturedSourceFingerprint` from that captured content, and revalidates the live source before publish. The captured and live fingerprints must both equal the confirmed fingerprint; otherwise staging is discarded and a new dry-run is required. The manifest binds the captured fingerprint and plan digest rather than copying an earlier probe result.
10. Winora reads the staged backup, validates its schema and SHA-256 hashes, atomically publishes it, and persists `BackupCreated`.
11. The user may cancel through backup verification. The first `RestorePointBeginRequested` or target-step `Applying` transition is the explicit boundary after which the UI no longer offers ordinary cancellation.
12. If the plan requires System Restore, the elevated host performs the complete lifecycle defined below. A verified new sequence must be durably recorded before any target mutation.
13. Immediately before every apply step, the executing process invokes that operation's conditional mutation primitive, which re-probes the exact current target and expected fingerprint while holding the documented transaction/handle/oplock protection used for the write. For privileged steps this occurs inside `Winora.ElevatedHost` after UAC. A mismatch persists `PlanInvalidatedNoChanges` if nothing committed or `RecoveryConflictExternalDrift` otherwise, stops the pipeline, and never overwrites the external change.
14. The executor persists the step's `Applying` transition and flushes it before the system action. It then performs one indivisible action, persists `Applied`, independently reads the resulting Windows state, and persists `Verified` before another system action is authorized.
15. After all steps are verified, any active restore-point sequence is finalized, the operation becomes `Completed`, and a sanitized action-journal event is emitted.
16. Verification failure opens an error result with a primary `Restore automatically` action. Rollback is offered, not executed silently.

Fingerprint validation is a precondition, not a one-time preview feature. Registry steps compare hive, canonical key/value name, value kind, and exact value bytes. File steps compare canonical path, stable file identity when available, last-write metadata, length, and content hash. The mutation lease prevents another Winora writer but does not assume other applications are idle.

Every direct operation must implement `IConditionalSystemMutation`: its final expected-state read and write/delete occur under a documented mechanism that prevents an unobserved third-party write from being overwritten, such as a supported transaction, an exclusive target handle/oplock, or an API with conditional-set semantics. A plain read followed by an unconditional set/delete and post-write verification is insufficient because it cannot detect a third-party write in the gap. If a mechanism cannot provide and pass that guarantee on the current Windows build/target type, capability probing reports `UnsupportedForSafeMutation` and Winora offers a guided/read-only path instead of direct apply. Post-write verification remains mandatory but is not treated as a substitute for conditional mutation.

### Partial execution

- Ordinary cancellation is unavailable after the first `RestorePointBeginRequested` or target-step `Applying` transition. An emergency stop request is honored only between steps and necessarily produces `PartiallyAppliedRecoveryRequired` once a target may have changed.
- An indivisible step is never interrupted halfway.
- The recovery screen lists completed, failed, and untouched steps and offers rollback of completed steps in reverse order.
- Closing the app does not erase the recovery record. A lost IPC connection makes the elevated host finish at most the already-authorized indivisible step, persist the best available result, and refuse every later step. On next launch, Dashboard and Changes show a persistent recovery InfoBar.
- If a crash occurs after a system mutation but before `Applied` is persisted, startup sees the last durable `Applying` transition, acquires the recovery lease, and probes the target. Matching proposed state can advance only that step to `Applied`/`Verified` after user-visible reconciliation. If the uncertain step still equals its backup, it becomes `ApplyStepNotApplied`; the whole operation becomes `ApplyFailedNoChanges` only when no earlier target step reached `Applied`, otherwise it becomes `PartiallyAppliedRecoveryRequired`. Any third state becomes `RecoveryConflictExternalDrift`.

### Rollback flow

1. User opens an applied or recovery-required change.
2. Winora acquires the same per-user mutation lease, probes current support, reads/verifies the linked backup, and compares the current fingerprint with the applied and backed-up fingerprints.
3. Unexpected external drift blocks automatic overwrite and opens a conflict review instead of guessing.
4. A rollback dry-run lists the exact values/files that will be restored.
5. User confirms. Winora atomically persists `RollbackPlanned` with the rollback-plan digest, source fingerprint, backup digest, ordered reverse-step IDs, and privilege requirements before requesting UAC or creating a checkpoint.
6. Immediately before the pre-rollback checkpoint, Winora revalidates support, the backup, and the live fingerprint and persists `RollbackPrepared`. A mismatch records `RecoveryConflictExternalDrift` and performs no checkpoint or reverse write.
7. Winora creates one bounded recovery checkpoint using the same captured-content snapshot algorithm as the operation backup: derive the checkpoint fingerprint from the captured bytes/values, re-read the live source, and require captured, live, and `RollbackPrepared` fingerprints to match before atomic publication. A mismatch discards staging and returns to conflict review. Only a verified committed marker permits `RollbackCheckpointCreated`. Recovery checkpoints do not recursively create more checkpoints.
8. Immediately before every rollback step, the executing process uses the same conditional mutation primitive to re-probe and protect the exact target through the reverse write. For privileged rollback this happens inside the elevated host after UAC. The current state must equal the expected applied state, the result of the preceding verified rollback step, or the backup state. Any other value records `RecoveryConflictExternalDrift` and opens conflict review instead of overwriting it.
9. Each reverse step persists `RollingBack` before mutation, `RollbackApplied` after mutation, and `RollbackVerified` after independent verification; every transition is flushed before the next reverse step.
10. If the current state already equals the backup, the step returns `AlreadyRestored` and is successful.
11. Repeating rollback is safe and produces the same final state.

### Startup recovery and reconciliation

Before Dashboard enables any mutating command, Winora scans durable operation journals for nonterminal states, waits for any validated surviving helper owner to exit, acquires the recovery mutation lease with a new epoch, and reconstructs the last verified boundary from immutable transitions. It does not infer success from the absence of an error or replay an `Applying`/`RollingBack` step.

- `Planned`, `Prepared`, or `BackupCreated` with no system lifecycle can be canceled safely after backup verification.
- `Applying`, `Applied`, `RollingBack`, or `RollbackApplied` is reconciled by an independent capability and target probe against the plan, backup, and per-step fingerprints.
- A fully matching target may continue to verification; a partial known state offers resume or rollback after a new review; external drift opens conflict review.
- `Verified` with every apply step verified completes only non-mutating bookkeeping: finalize an active restore lifecycle if needed, rebuild the action/applied-change projections, then persist `Completed`. If only some steps are verified, it becomes `PartiallyAppliedRecoveryRequired`.
- `RollbackPlanned`, `RollbackPrepared`, or `RollbackCheckpointCreated` confirms that rollback intent/checkpoint exists but authorizes no blind reverse write; the user reviews the reconstructed rollback before continuation.
- `RollbackVerified` with every reverse step verified rebuilds rollback projections and persists `RolledBack`; a partially verified rollback remains `RollbackFailedRecoveryRequired`.
- `RestorePointBeginRequested`, `RestorePointBeginReturnedUnverified`, `RestorePointBegun`, `RestorePointEndRequested`, `RestorePointCancelRequested`, or a finalization-unknown state invokes the System Restore recovery procedure below before another high-risk operation.
- An abandoned interprocess lease is evidence of possible process death, not permission to resume automatically. Recovery is shown to the user and all planned writes require fresh confirmation/fingerprint validation.

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
├─ Operations\
│  └─ {operation-id}\
│     ├─ manifest.json
│     └─ Transitions\{revision}-{transition-id}.json
├─ Journal\
│  ├─ index.json
│  └─ Events\{event-id}.json
├─ Assets\
│  └─ managed copies of user-selected .ico/.wav/.cur/.ani files
└─ Pending\
   └─ non-authoritative short-lived IPC rendezvous metadata
```

### Atomic JSON writing

- Serialize into a uniquely named temporary file in the destination directory, flush managed buffers, call the OS durable flush (`FileStream.Flush(true)`/`FlushFileBuffers`), then read the temporary file back, validate schema, and compute hashes.
- Mutable projection files use `File.Replace`/`ReplaceFileW` with a last-known-good file, followed by reopening and flushing the published target and readback verification. Projections are caches; if a power-loss window leaves them missing/stale, they are rebuilt from authoritative immutable events.
- A new authoritative operation/action transition is published with a same-volume `MoveFileExW` using `MOVEFILE_WRITE_THROUGH`, then reopened, schema/hash verified, and flushed again. The transition is not acknowledged as durable and no system action may start until this write-through publication completes.
- Retain the last-known-good replacement file until the new target passes post-publication readback.
- Use both a process-local async lock and a global per-user interprocess persistence mutex so `Winora.App` instances in different logon sessions and `Winora.ElevatedHost` cannot allocate the same revision or interleave replace/index operations.
- Backup directories are built under `{id}.staging`, fully verified, then renamed to `{id}` on the same volume. A write-through atomically published `manifest.committed.json` marker is created only after the rename; recovery treats a directory without that verified marker as unpublished staging and never uses it for apply/rollback.

All persisted documents have `schemaVersion`, `createdUtc`, and a stable identifier. Infrastructure owns DTOs and migrations; Core models do not contain JSON attributes.

### Interprocess mutation lease and durable operation journal

- `Winora.App` is single-instanced per user/logon session with Windows App SDK `AppInstance`; secondary activations in that session redirect arguments to the primary instance.
- Every apply, rollback, restore-point lifecycle, or recovery action additionally requires a global per-user mutation lease identified as `Global\Winora.Mutation.{UserSidHash}` with an ACL limited to the initiating user SID and SYSTEM. A short-held global coordination mutex serializes compare-and-swap updates to the durable lease record; it is not itself the full-operation lease.
- Lease metadata records a random lease ID/epoch, operation ID, current durable revision, acquisition/heartbeat times, and an owner set of validated PID/process-start-time/user-SID/package-role tuples. It contains no bearer authorization secret. Before any privileged action, the authenticated helper atomically joins the owner set and persists its heartbeat; the App does not authorize the step until that join is durable.
- The lease remains active while any validated owner process is alive. If the App dies, the elevated helper remains an active owner, may finish only its already-authorized indivisible step, persists the best-known result, and exits; a replacement App cannot acquire recovery ownership or inspect a mid-mutation target until that helper has exited or been proven dead.
- Stale takeover is allowed only after every recorded owner is proven absent by PID plus process-start-time and package/signature validation. The next process advances the lease epoch, scans incomplete journals, and enters recovery before enabling mutation. An abandoned coordination mutex alone never authorizes takeover.
- A second instance or helper that cannot validate/join the active lease returns `OperationBusy`; it cannot start a parallel apply/rollback or publish a conflicting transition.
- `Operations` is distinct from the user-facing sanitized action journal. Its immutable transition events are the authoritative write-ahead record for recovery; `manifest.json` is an atomically replaceable projection and can be rebuilt from the transition chain.
- Each transition contains operation/step ID, monotonic revision, previous-event hash, status, actor (`App` or `ElevatedHost`), timestamp, plan digest, expected/result fingerprint, typed error code, and System Restore lifecycle data when applicable. It excludes secrets, command lines, raw exceptions, and display-unsafe values.
- The process that will perform a system action must publish and verify the pre-action transition itself. For privileged actions the elevated helper writes only through `IDurableOperationJournal`; it derives the operation directory from the validated operation ID and fixed Winora root and never accepts an arbitrary persistence path.
- A crash after `Applying`/`RollingBack` but before `Applied`/`RollbackApplied` is explicitly representable. Startup reconciliation probes the target and advances, rolls back, or reports conflict; it never assumes the mutation did or did not happen.

### Action journal

- Stored separately as one immutable, atomically published JSON document per event; `index.json` is a rebuildable cache.
- Each event uses the same temp → flush → readback → atomic-move protocol as other JSON documents.
- Contains timestamp, operation identifier, category, status, risk, privilege class, support status, and correlation ID.
- Never stores tokens, environment variables, command lines, registry data dumps, full user paths, file contents, usernames, or raw exception payloads.
- User-visible labels replace paths; a salted local hash may correlate the same target without revealing it.

Backups may contain the minimum exact paths and values needed for rollback. They are not copied into the journal and inherit user-only filesystem ACLs.

### Retention defaults

- Never delete a backup linked to an active or recovery-required change.
- Never delete an incomplete durable operation journal. A completed journal is retained at least as long as its linked change and rollback backup; deleting an eligible completed journal and its backup is one atomic retention decision recorded in the action journal.
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
| Visual-effect preferences (expanded set) | One operation instance per documented `SystemParametersInfo` get/set action pair, with the documented broadcast | Standard | Full | None | Supported per action when that action exists on the running build |
| Taskbar and Start (per-user) | Documented `HKCU ...\Explorer\Advanced` scalar values plus the documented shell change notification | Standard | Full | Explorer or sign-out | Supported only when the live value kind matches the documented semantics; otherwise guided |
| Taskbar size, autohide, pinned-item order | Read-only status with a stable reason code and a guided route | Standard | — | — | Unsupported: `TaskbarSi` is undocumented and disabled, `StuckRects3` is a documented opaque blob, `Taskband\FavoritesMigration` is explicitly undocumented |
| Temporary-file reclamation (per-user) | Enumerate with documented `GetTempPath2W`/`SHGetKnownFolderPath`, then move into `%LOCALAPPDATA%\Winora\Quarantine\{operationId}` with `IFileOperation::MoveItem` | Standard | Full | None | Supported only for user-owned locations on the same volume as `%LOCALAPPDATA%`; otherwise guided |
| Quarantine purge and Winora journal/backup retention | Not a `ChangeCoordinator` operation. An explicit retention decision over Winora-owned data, recorded in the action journal | Standard | Not applicable | None | Supported; never applied to a quarantine still linked to a recovery-required change |
| Windows event-log channels | Read channel metadata; export-then-clear through documented `EvtClearLog` with a non-null target file | Admin for system channels | Not applicable | None | Guided/unsupported in MVP; direct apply only after the elevated host and restore-point lifecycle exist |
| Cursor scheme authoring | Write a documented `.theme`-format cursor scheme into `%LOCALAPPDATA%\Winora\Assets` and hand off with documented `ShellExecute` on that file | Standard | Full for the Winora-owned file | None | Guided for the persistent Windows change; the live `HKCU\Control Panel\Cursors` scheme has no Learn documentation and stays out of scope |

Every table row marked direct `Supported` is additionally conditional on a passing `IConditionalSystemMutation` capability probe for the concrete Windows build and target. If Winora cannot protect the final expected-state check through the write, the row degrades to `UnsupportedForSafeMutation`/guided for that target instead of using an unconditional fallback.

Two documentation honesty rules constrain the rows above. First, the Windows 11 settings reference states its purpose as *reading* settings for backup and data portability, and it lists taskbar values as `REG_SZ` under `SystemSettings_*` names while the live registry uses simple DWORDs such as `TaskbarAl`; a probe must therefore compare the documented kind against the live `RegistryValueKind` and degrade to `Unknown`/guided on a mismatch rather than guessing. Second, an exported `.evtx` is evidence, not a restore: Microsoft documents no way to re-inject records into a live channel, so log clearing can never claim `RollbackCapability.Full`.

Reclamation copy must not overstate what these operations do. The `SystemParametersInfo` rows change visual effects and perceived responsiveness; they are not a performance tuner and must never be labelled as one.

### Restore-point policy by operation

- `Required for forward apply`: direct enable/disable of an `HKLM ...\Run` value and moving a shortcut to/from the common Startup folder. These are machine-wide startup changes; forward apply is blocked unless a verified local backup and a new Winora-owned restore-point sequence are both available. Rollback does not demand a second new restore point, which Windows may coalesce within 24 hours; it uses the verified operation backup plus the bounded pre-rollback checkpoint and retains the original Winora restore point when it still exists.
- `Local backup only`: documented `SystemParametersInfo` visual effects, documented per-user taskbar and Start values, the reversible move into the Winora quarantine, `HKCU ...\Run`, the per-user Startup folder, folder `Desktop.ini` icons, and shortcut icon changes. These have exact operation-specific backups and full rollback but do not request UAC solely to create a restore point.
- `Neither`: Winora theme, preview/read-only inspection, capability probes, dry-run, and guided Windows-owned settings routes because they do not directly mutate Windows through Winora.
- `Restore-point operation`: the explicit manual restore-point command is itself a non-destructive safety artifact. It uses the complete lifecycle below but does not recursively create a local operation backup or another restore point.

Risk and privilege are separate: an administrator requirement does not automatically imply a restore point, and a restore point never substitutes for the exact local backup used by rollback.

### Registry policy

Registry access is allowed only when a Microsoft document identifies the key as a supported integration point, such as Run/RunOnce. Each implementation source file includes a link to its supporting Microsoft documentation. If that documentation cannot be cited, the operation is not implemented as a direct mutation.

### PowerShell policy

No PowerShell is required for the fixed MVP operation catalog. If a later operation genuinely needs it, it must use a versioned script owned by Winora, fixed parameters, strict path validation, no interpolated command strings, and redacted output. ViewModels can never invoke it.

## 9. Administrator and elevation model

- The Winora App process starts and remains a standard-user process.
- `IPrivilegeService` evaluates the confirmed plan, not the current page.
- Standard-user plans never trigger UAC.
- MVP elevation supports consent elevation only for the same interactive account when that account has an administrator split token. The capability probe checks this before review. A true standard-user account that would require over-the-shoulder administrator credentials receives `UnsupportedForCurrentAccount` with an exact explanation and no UAC launch, because a helper running under another SID/profile cannot safely join the initiating user's lease or `%LOCALAPPDATA%` journal. Supporting a separate-credential broker is explicitly post-MVP.
- Supported administrator plans are confirmed in the UI before UAC appears.
- The package manifest contains the restricted `runFullTrust` and `allowElevation` capabilities; `Winora.ElevatedHost.exe` has its own `requireAdministrator` executable manifest. Release packaging tests must prove that the signed MSIX can launch only that helper with UAC and that the main executable remains medium integrity.
- Before UAC, the App creates a first-instance, single-connection, local-only named-pipe server with a cryptographically random name/nonce, a short expiration, and an ACL limited to the initiating user SID and SYSTEM. Remote pipe clients are rejected.
- The App launches the package-installed helper with `ShellExecuteEx("runas")`. The command line carries no operation payload. If UAC returns `ERROR_CANCELLED`, the operation records `ElevationCanceledNoChanges`, closes the pipe, releases the lease, and performs no restore-point begin or target mutation.
- Caller identity is mutually verified before any request is accepted. The helper obtains the pipe-server PID and validates its user SID, logon session, package family identity, executable path inside the installed Winora package, and package publisher/signature. The App obtains the pipe-client PID and validates high integrity, the same user SID/logon session produced by consent elevation, the exact packaged helper path, and the same publisher/signature. A PID alone or a matching filename is insufficient; a different credential SID is rejected and should already have been blocked by capability probing.
- After mutual identity validation, the peers negotiate an explicit `protocolMajor.protocolMinor`; an unknown major fails closed. The App generates an ephemeral one-session key, sends it only over the mutually authenticated pipe, and both peers authenticate canonical request/response envelopes with HMAC-SHA-256; the key is never persisted and is zeroed on teardown. Envelopes include message ID, correlation ID, nonce, expiry, operation/step ID, plan digest, expected fingerprint, mutation-lease ID, and payload schema version; replayed IDs/nonces are rejected.
- The helper maps `operationId` to a compiled allowlist handler and validates the handler-specific DTO. It cannot dispatch a registry path, filesystem path, COM method, executable, script, or shell command that is not produced and validated by that handler.
- After elevation and immediately before every privileged apply/rollback step, the helper independently reruns OS/API support, privilege, canonical-path, target-type, source-fingerprint, backup binding, and active-lease validation. Any mismatch produces a typed no-write result and requires a new dry-run or conflict review.
- The versioned result stream reports durable state revision, stage, step ID, typed status/error, support status, resulting fingerprint, verification facts, restart requirement, and System Restore sequence/finalization facts. Raw exceptions, secrets, and sensitive full paths are never transmitted or logged.
- If IPC disconnects, the helper may finish only an indivisible step whose `Applying`/`RollingBack` transition it already persisted. It must persist the best-known result, refuse all subsequent steps, close any owned restore lifecycle as described below, and exit.
- Winora never changes `SystemRestorePointCreationFrequency` or enables System Protection through a hidden policy mutation.

### System Restore lifecycle

System Restore is a paired protocol, not a fire-and-forget command. The durable operation journal records `restorePointCorrelationId`, the Winora-tagged description, `sequenceNumber` when known, begin/finalize timestamps, API status, ownership/new-point verification, and finalization mode. The lifecycle states are:

```text
RestorePointBeginRequested
  → RestorePointBeginReturnedUnverified
  → RestorePointBegun
  → RestorePointEndRequested
  → RestorePointEnded

or, before any target mutation:

RestorePointBeginRequested
  → RestorePointBeginReturnedUnverified
  → RestorePointBegun
  → RestorePointCancelRequested
  → RestorePointCancelled
```

1. For a target-mutation operation, the local backup must already be `BackupCreated`. The explicit manual restore-point artifact instead must be `Prepared` with `backupRequirement: NotApplicable`. In both cases the mutation lease must be active, System Protection/capability must be supported, and no unresolved Winora restore lifecycle may exist.
2. The elevated helper inventories the documented current restore points, then persists and flushes `RestorePointBeginRequested` with that pre-begin inventory fingerprint before calling `SRSetRestorePointW` with `BEGIN_SYSTEM_CHANGE` and a unique Winora correlation tag.
3. On API success, it immediately persists `RestorePointBeginReturnedUnverified` with the returned sequence number. It then proves from the pre-begin inventory, returned sequence, correlation tag, and post-call inventory that this is a genuinely new Winora-owned point; only proof advances to `RestorePointBegun` and authorizes a target mutation. If Windows reuses an older point, Winora performs the documented safe close for the begin call, records `RestorePointFailedNoChanges`, and blocks the target operation; it never changes `SystemRestorePointCreationFrequency` and never cancels/removes a pre-existing point.
4. After all target steps are verified, the helper persists `RestorePointEndRequested`, calls `SRSetRestorePointW` with `END_SYSTEM_CHANGE` and the original sequence number, then persists `RestorePointEnded`. Only then may the operation become `Completed`.
5. If the user cancels or an error occurs after a Winora-owned begin but before any target mutation, the helper persists `RestorePointCancelRequested` and closes with `END_SYSTEM_CHANGE` plus `CANCELLED_OPERATION` using the original sequence; success becomes `RestorePointCancelled`.
6. If any target mutation committed or its result is uncertain, failure/emergency stop must retain the safety point: Winora persists `RestorePointEndRequested`, attempts normal `END_SYSTEM_CHANGE`, then proceeds to verification/rollback. It must not remove or mark the point cancelled.
7. An exception after begin always enters a `finally`-equivalent finalization path. A finalization call that returns a documented failure is persisted as `RestorePointFinalizeFailedRecoveryRequired`; further high-risk operations remain blocked and a reviewed retry requests UAC only for finalization. A process crash or lost result after the call started but before its terminal transition is persisted/recovered as `RestorePointFinalizeOutcomeUnknown`, not assumed to have failed.

Crash recovery is explicit:

- `RestorePointBeginRequested` without a sequence means the helper may have crashed during the API call. No target step may have been authorized. On startup, Winora detects the state, obtains elevation, and uses documented System Restore inventory plus the unique correlation tag/time window to locate a candidate. If exactly one candidate is found, it persists `RestorePointBeginReturnedUnverified` and performs the ownership proof below; if no unique candidate exists, Winora does not guess, blocks high-risk mutation, and provides a recovery result with manual System Restore guidance.
- `RestorePointBeginReturnedUnverified` contains a sequence but does not establish ownership. Startup repeats only the read-only inventory proof. If the point is proven new and Winora-owned, it advances to `RestorePointBegun`; if it is proven reused/pre-existing, Winora never sends `CANCELLED_OPERATION` or removes it and uses only the documented non-cancelling close for the begin call; ambiguity becomes `RestorePointRecoveryRequired` with no destructive finalization guess.
- `RestorePointBegun` with no finalization-request transition is finalized as cancelled only when the durable journal proves that no target step reached `Applying`; otherwise it is finalized normally and retained.
- `RestorePointEndRequested` or `RestorePointCancelRequested` without a terminal result is an ambiguous crash window. Microsoft documents BEGIN/END pairing but does not guarantee that repeating END/CANCEL is idempotent. Winora first uses documented System Restore inventory/status evidence to prove the outcome. If completion can be proven, it persists the matching terminal state; if it cannot, it records `RestorePointFinalizeOutcomeUnknown`, does not call END/CANCEL again automatically, blocks further high-risk operations, and presents the safe manual recovery route.
- A terminal `RestorePointEnded`/`RestorePointCancelled` is never finalized again.
- Startup performs restore-lifecycle reconciliation before ordinary incomplete-operation reconciliation, because the stored sequence determines whether recovery may safely continue.

The explicit manual restore-point command uses the same BEGIN/END pair and durable sequence handling. It becomes successful only after `RestorePointEnded`; a successful BEGIN alone is not reported as completion.

## 10. Navigation and screens

### `NavigationView`

```text
Главная

ПЕРСОНАЛИЗАЦИЯ
  Темы и эффекты
  Панель задач
  Звуки
  Курсоры
  Иконки

ОБСЛУЖИВАНИЕ
  Производительность
  Очистка

СИСТЕМА
  Автозагрузка
  Изменения
  Резервные копии

Footer
  Журнал действий
  Настройки
```

Route keys are stable, lowercase, and kebab-cased; they are never derived from a localized label. The registry covers every pane item plus the route-only screens in section 10: `dashboard`, `themes`, `taskbar`, `performance`, `cleanup`, `sounds`, `cursors`, `icons`, `startup`, `changes`, `backups`, `journal`, `settings`, `compatibility`, `change-review`, `rollback-review`, `applying`, `result-success`, `result-failure`, `result-partial-recovery`, and `recovery`.

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
- App startup checks durable operation journals and restore-point lifecycle states before enabling Dashboard mutation commands. Incomplete work opens a recovery route with the last durable boundary, observed current state, safe choices, and any required UAC; it is never silently resumed.

## 15. Testing strategy

### Core tests

- Change-plan digest stability.
- Source-fingerprint drift invalidates a previously confirmed plan before backup, before every apply step, and before every rollback step.
- Every state-machine transition, including cancellation before apply.
- Policy blocks unsupported, unknown, partial/no-rollback, and high-risk-without-restore-point plans.
- Partial-operation recovery ordering.
- Idempotent rollback and `AlreadyRestored` behavior.
- Durable transition rules reject skipped revisions, a second system action before the prior step is durably `Applied`/`Verified`, and replay of an uncertain `Applying` step.
- Restore-point lifecycle policy covers begin, successful end, cancellation before target mutation, normal end after partial mutation, known finalization failure, ambiguous finalization outcome, and startup recovery.

### Infrastructure tests

- Atomic create and replace.
- Interrupted staging write never replaces the last-known-good file.
- Readback/schema/hash verification failures.
- Concurrent-write serialization across two processes, including competing transition revisions from App and ElevatedHost.
- Backup-directory publish and corruption detection.
- Atomic per-event journal publication, index rebuild, retention, and redaction rules.
- Schema migration from every supported version.
- Crash injection after `Applying` is flushed, after the system adapter mutates, before `Applied` is flushed, and between verified apply steps; journal reconstruction must identify the exact uncertain boundary.
- Publication fault tests cover temp flush, write-through rename, post-publication readback, projection replace, and committed-marker creation; no pre-action transition is acknowledged before the authoritative file survives reopen/hash verification, and stale projections rebuild from events.
- Rollback-checkpoint race tests mutate and restore the source during capture; a captured fingerprint that differs from `RollbackPrepared` is rejected even when the later live value matches again.
- Abandoned mutation-lease recovery blocks new mutation until incomplete journals are reconciled.

### System tests

- Unit tests use narrow fake Windows adapters, not mocked ViewModels.
- Capability matrices for supported/unsupported OS builds and privileges.
- Canonical path, extension, writable-target, and protected-target validation.
- Conditional-mutation race tests place an external writer after the final expected-state read; the Winora write must fail without overwriting it. An operation/target that cannot provide this guarantee reports `UnsupportedForSafeMutation`.
- Backup snapshot tests mutate the source during capture; Winora discards staging when the captured/live/confirmed fingerprints are not identical.
- Folder `Desktop.ini` and shortcut Shell Link round-trip against temporary test assets.
- Run-key operations use a dedicated Winora test location by default; opt-in Windows integration tests may create and clean a uniquely named HKCU Run value.
- No default test changes production user settings.
- Elevated-host contract tests reject unknown protocol versions/operation IDs, expired/replayed nonces, caller/package/signature mismatch, inactive lease IDs, changed fingerprints after UAC, arbitrary command/path payloads, and malformed result envelopes.
- Privilege tests prove that same-account split-token consent elevation is supported and a true standard-user/alternate-credential scenario is reported as `UnsupportedForCurrentAccount` before UAC or IPC creation.
- System Restore adapter tests inject crashes after `BEGIN_SYSTEM_CHANGE`, before sequence persistence, after `RestorePointBeginReturnedUnverified`, and before/during/after `END_SYSTEM_CHANGE` or cancellation; recovery never cancels/removes a reused sequence, never guesses ambiguous ownership, never blindly repeats an END/CANCEL with unknown outcome, and never starts a target mutation first.

### App tests

- Every NavigationView item, Dashboard card, CommandBar item, activity row, confirmation action, error action, and placeholder has a bound command or route.
- ViewModel tests prove draft-only behavior before confirmation.
- Confirmation and rollback review copy contains risk, rights, rollback, and restart facts.
- Navigation contract tests fail when a route key is unregistered.
- A XAML interaction audit flags Button/Card controls without behavior or explicit disabled help text.
- A second activation is redirected to the primary App instance; a deliberately separate test process holding the mutation lease makes apply/rollback report `OperationBusy` without opening UAC or writing Windows.
- UAC cancellation produces `ElevationCanceledNoChanges`, leaves no restore-point begin or target mutation, and keeps the verified local backup inspectable.
- Startup UI for an incomplete operation offers only choices supported by reconciliation: verify/complete, rollback, conflict review, restore-point finalization, or explicit no-change cancellation.

### Build and manual release checks

- `dotnet test` for non-UI projects.
- MSBuild 18 `/restore` x64 Debug and Release.
- Packaged app deployment and launch on the local Windows 11 25H2 machine.
- Primary navigation and dry-run/rollback smoke flow.
- Two-process smoke test proves that concurrent Winora launches cannot execute parallel apply/rollback.
- Crash-recovery smoke tests cover: crash after a system mutation before result persistence, crash between apply steps, external mutation after dry-run and after UAC, UAC cancellation, crash after `BEGIN_SYSTEM_CHANGE`, restart recovery of an incomplete operation, and repeated rollback returning `AlreadyRestored`.
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
- The signed MSIX declares and validates `runFullTrust`/`allowElevation`, packages the non-UI elevated helper separately, and is distributed through the fixed sideload/enterprise channel.
- Elevated IPC is versioned, mutually authenticates caller/package identity, dispatches only compiled allowlisted operations, and rejects arbitrary commands or paths.
- Alternate-credential elevation is blocked before UAC in MVP; supported elevation uses the same account's split administrator token.
- Single-instance activation plus the per-user interprocess mutation lease prevents parallel apply/rollback across App and helper processes.
- For every target-mutating operation, a durable crash-safe operation journal records `Planned → Prepared → BackupCreated → Applying → Applied → Verified → Completed` and all error/rollback/restore-point transitions before a later system action can start; the manual restore-point safety artifact uses the documented `backupRequirement: NotApplicable` path.
- A failed verification offers automatic rollback and retains a recovery record if rollback fails.
- Fingerprints are revalidated before backup, every apply step, and every rollback step, including inside the elevated host after UAC; external drift is never overwritten silently.
- Direct operations pass a mechanism-specific conditional-mutation race test; otherwise they report `UnsupportedForSafeMutation` rather than performing an unconditional write.
- Every Winora-owned `BEGIN_SYSTEM_CHANGE` is durably correlated with its sequence and reaches a documented successful `END_SYSTEM_CHANGE`, cancellation end, or explicit recovery-required state before another high-risk operation.
- Startup detects and reconciles incomplete apply, rollback, elevation, and System Restore lifecycles without blindly replaying an uncertain step.
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
- [App capability declarations (`runFullTrust`, `allowElevation`)](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)
- [App instancing with the Windows App SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-instancing)
- [Sign an app package using SignTool](https://learn.microsoft.com/en-us/windows/msix/package/sign-app-package-using-signtool)
- [MoveFileExW (`MOVEFILE_WRITE_THROUGH`)](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)
- [FlushFileBuffers](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-flushfilebuffers)
- [Common Windows settings: personalization colors](https://learn.microsoft.com/en-us/windows/apps/develop/settings/settings-common#personalization---colors)
- [SystemParametersInfoW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow)
- [Run and RunOnce registry keys](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)
- [Theme File Format](https://learn.microsoft.com/en-us/windows/win32/controls/themesfileformat-overview)
- [How to customize folders with Desktop.ini](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-customize-folders-with-desktop-ini)
- [IShellLink::SetIconLocation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishelllinkw-seticonlocation)
- [System Restore API](https://learn.microsoft.com/en-us/windows/win32/sr/system-restore-api)
- [System Restore WMI classes](https://learn.microsoft.com/en-us/windows/win32/sr/system-restore-wmi-classes)
- [Calling SRSetRestorePoint](https://learn.microsoft.com/en-us/windows/win32/sr/calling-srsetrestorepoint)
- [Reference for Windows 11 settings](https://learn.microsoft.com/en-us/windows/apps/develop/settings/settings-windows-11)
- [SHChangeNotify](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shchangenotify)
- [GetTempPath2W](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-gettemppath2w)
- [SHGetKnownFolderPath](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath)
- [IFileOperation::MoveItem](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-moveitem)
- [FILEOPERATION_FLAGS](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/ne-shobjidl_core-_fileoperation_flags)
- [EvtClearLog](https://learn.microsoft.com/en-us/windows/win32/api/winevt/nf-winevt-evtclearlog)
- [Launch the Windows Settings app](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)

Mechanisms deliberately excluded, recorded so a later reader does not mistake the omission for an oversight: [IEmptyVolumeCache2](https://learn.microsoft.com/en-us/windows/win32/api/emptyvc/nn-emptyvc-iemptyvolumecache2) and [Creating a Disk Cleanup Handler](https://learn.microsoft.com/en-us/windows/win32/lwef/disk-cleanup) document the contract a cleanup *handler* implements for `cleanmgr`, not a client API Winora may drive; [Storage policy CSP](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-storage) documents Storage Sense only as machine policy. There is no documented client API to run Disk Cleanup or trigger Storage Sense once, and no Learn page documents the live `HKCU\Control Panel\Cursors` scheme.
