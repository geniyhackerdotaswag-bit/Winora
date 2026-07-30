# Winora Capability Extension Plan

**Goal:** Add four capability domains the user asked for — visual-effect/performance preferences, per-user taskbar and Start values, temporary-file reclamation, and cursor scheme authoring — without weakening the safety core. Every domain is one `IOperation` in `Winora.System/Operations/` plus one narrow documented adapter. This plan is additive to `2026-07-13-winora-mvp-implementation.md`; the numbering continues from that plan's Task 10.

**Source of truth:** `docs/superpowers/specs/2026-07-12-winora-design.md` sections 3, 8, 10, and 17 as amended on 2026-07-30, plus the "Irreversible byte reclamation" section of `docs/safety-model.md`.

## Global constraints beyond the base plan

- Reuse `ChangeCoordinator`, `ChangeSafetyPolicy`, `OperationCapabilityPolicy`, `CapabilityBlockCodes`, `BackupRepository`, `WinoraDataPaths`, and the existing retention machinery. A new domain that appears to need a second coordinator or plan type is a signal the mechanism is not documented well enough to ship.
- `VisualEffectsOperation` is the reference implementation. Copy its shape: `IOperation` + `IConditionalSystemMutation`, one instance per target, Learn URI inline, `OutcomeUnknown` for unattributable failures, refuse drift, and never plan a step whose target already holds the proposed value.
- **Every domain in this plan is `PrivilegeRequirement.StandardUser`, `RiskLevel` at most `Medium`, and `requiresRestorePoint: false`.** `ChangeCoordinator.cs:102-116` hard-blocks any plan requiring the restore-point lifecycle, and `ChangeFacts.cs` blocks `Risk == High` without one, so anything higher would durably journal itself as `Unsupported`. Machine-wide domains wait for Task 5.
- Step identifiers must satisfy `ChangePlan.IsSafeStepId`: lowercase ASCII, digits, `-`/`_` after position 0, at most 64 characters. They cannot reuse the dotted catalog operation id.

---

### Task 11: Expanded visual-effect preferences

**Why first:** no elevation, one Learn page, one get/set pair per setting, an availability probe already proven against `ERROR_INVALID_PARAMETER`, and a single-BOOL backup that the existing `BackupRepository` stores verbatim. Zero new machinery.

**Files:**
- Modify: `src/Winora.System/Windows/VisualEffectsAccess.cs` — extend `VisualEffectSetting` and the action-constant table.
- Modify: `src/Winora.System/Operations/VisualEffectsOperation.cs` — extend `IdFor` and `StepIdFor`.
- Modify: `tests/Winora.System.Tests/Windows/WindowsVisualEffectsAccessTests.cs`, `tests/Winora.System.Tests/Operations/VisualEffectsOperationTests.cs`.

Actions to add, all from `SystemParametersInfoW`: `ANIMATION`, `MENUANIMATION`, `COMBOBOXANIMATION`, `LISTBOXSMOOTHSCROLLING`, `TOOLTIPANIMATION`, `TOOLTIPFADE`, `MENUFADE`, `SELECTIONFADE`, `CURSORSHADOW`, `DROPSHADOW`, `FONTSMOOTHING`, `DRAGFULLWINDOWS`. `MENUSHOWDELAY` is an integer, not a toggle — either give it its own value kind or defer it; do not silently coerce it to a BOOL.

- [ ] **Step 1: Write failing tests** — one theory case per new setting asserting a distinct operation id, a distinct safe step id, and correct get/set action pairing; a case proving an absent action degrades rather than throws.
- [ ] **Step 2: Verify RED**
- [ ] **Step 3: Implement** the setting enum, action table, and id mappings.
- [ ] **Step 4: Verify GREEN and confirm no copy promises speed** — these are visual effects, not a performance tuner.

---

### Task 12: Per-user taskbar and Start values

Introduces the registry adapter that Task 4's `HKCU ...\Run` work later reuses, so its shape matters beyond this domain.

**Files:**
- Create: `src/Winora.System/Windows/UserShellPreferenceAccess.cs` — documented `HKCU ...\Explorer\Advanced` reads/writes plus `SHChangeNotify`, Learn URIs inline.
- Create: `src/Winora.System/Windows/DocumentedShellValues.cs` — the value catalog with each value's documented kind and allowed set.
- Create: `src/Winora.System/Operations/UserShellPreferenceOperation.cs`.
- Create: `tests/Winora.System.Tests/Windows/UserShellPreferenceAccessTests.cs`, `tests/Winora.System.Tests/Operations/UserShellPreferenceOperationTests.cs`.

In scope, and confirmed against the live registry before being encoded: `TaskbarAl`, `ShowTaskViewButton`, `TaskbarDa`, `TaskbarGlomLevel`, `MMTaskbarGlomLevel`, `MMTaskbarEnabled`, `MMTaskbarMode`, `Start_Layout`, `Start_TrackDocs`, `Start_IrisRecommendations`. `TaskbarSd`, `TaskbarSn` and `TaskbarBadges` were dropped from the earlier draft list: they are not described by the reference page with a documented allowed set, and a plausible-looking name is not documentation.

Excluded with the reason recorded in the catalog source: `TaskbarSi`, `StuckRects3`, `Taskband\FavoritesMigration`, `UserPreferencesMask`, `VisualFXSetting`. The catalog is the boundary — an excluded name has no code path at all, which a test asserts.

**Absence is a state.** Eight of the ten values do not exist on a fresh profile; Windows then applies its own default. Absence is therefore modelled as the first-class value `unset`, and restoring it deletes the value rather than writing a number Winora chose. Writing a guessed default would leave the registry in a shape the user never had while reporting a successful rollback.

- [x] **Step 1: Write failing tests**
- [x] **Step 2: Verify RED**
- [x] **Step 3: Implement** the adapter, catalog, and operation.
- [x] **Step 4: Verify GREEN** — 163 System tests.
- [x] **Step 5: Prove the write is not virtualized. Redirection was real and is now disabled.** Applying `ShowTaskViewButton` through the packaged app left the value at `0` in the real `HKCU\...\Explorer\Advanced` when read from a separate unpackaged process, while the package's private hive (`SystemAppData\Helium\User.dat`) existed and absorbed the write. Because MSIX registry redirection is copy-on-write, the app's own verification read hit its own write and could not detect this. The manifest now declares `windows.registryWriteVirtualization` as `disabled` with the required `unvirtualizedResources` capability. **Re-verify after any manifest change**: this is the one defect class the safety pipeline cannot catch by itself, since every layer behaves correctly and still reports success.
- [ ] **Step 6: Confirm the screen** — every row shows its live value or an explicit reason, `unset` is offered, and preview stays disabled until the selection differs from the observed value.

---

### Task 13: Temporary-file reclamation via quarantine

Requires the two-part split from `docs/safety-model.md`. Part 2 reuses the retention machinery that already exists in `Winora.Infrastructure/Journal/`; do not write a second one.

**Measured on 2026-07-30, and it changes the design.** File writes from the packaged app are *not* virtualized: a probe run inside the package container wrote to the real `C:\Users\...\AppData\Local\Temp` and an unpackaged process saw the file. But `%LOCALAPPDATA%` *is* redirected — Winora's own backups and journals land under `LocalCache\Local\Winora`. So a quarantine under `%LOCALAPPDATA%\Winora` would live inside the package's redirected storage, which **Windows deletes when the package is uninstalled**. Quarantined items are the user's files, so that would silently destroy data on uninstall. Resolve the quarantine location before moving a single byte: either place it outside package-scoped storage, or make uninstall-time loss impossible by refusing to quarantine anything the user has not already agreed to lose. Do not treat this as a detail.

**Files:**
- Modify: `src/Winora.Infrastructure/Paths/WinoraDataPaths.cs` — add the owned `Quarantine/{operationId}` layout with the existing traversal guards, once the location question above is settled.
- Create: `src/Winora.System/Windows/TempLocationProbe.cs` — `GetTempPath2W` + `SHGetKnownFolderPath` enumeration, volume and ownership checks, protected-target list.
- Create: `src/Winora.System/Windows/ShellFileOperationAccess.cs` — `IFileOperation::MoveItem` with documented flags.
- Create: `src/Winora.System/Operations/QuarantineMoveOperation.cs`.
- Create: matching tests under `tests/Winora.System.Tests/` and `tests/Winora.Infrastructure.Tests/Paths/`.

- [ ] **Step 1: Write failing tests** — same-volume move is a rename and space-neutral; cross-volume degrades to guided; `%WINDIR%\Temp`, `SoftwareDistribution`, CBS logs, and `Windows.old` are `TargetProtected` and never enumerated as targets; rollback returns items and reports `AlreadyRestored` when already home; verification re-hashes at the destination; a purge is refused while the quarantine is linked to a recovery-required change.
- [ ] **Step 2: Verify RED**
- [ ] **Step 3: Implement** the probe, adapter, operation, and the retention wiring for the purge.
- [ ] **Step 4: Verify GREEN**

---

### Task 14: Cursor scheme authoring

**Files:**
- Create: `src/Winora.System/Windows/ThemeFileWriter.cs` — documented `.theme` `[Control Panel\Cursors]` authoring with every documented role name.
- Create: `src/Winora.System/Operations/CursorSchemeAuthoringOperation.cs`.
- Create: matching tests.

The live `HKCU\Control Panel\Cursors` scheme has no Learn documentation, so Winora writes only its own `.theme` file under `%LOCALAPPDATA%\Winora\Assets` and hands off with documented `ShellExecute`. Writing `.cur` files into `%SystemRoot%` is a protected target and out of scope. This domain needs the payload-capable backup (`IBackupCaptureProvider` + `BackupPayloadStore`), which already exists.

- [ ] **Step 1: Write failing tests** — every documented role name round-trips; the Winora-owned file has full rollback; the persistent Windows change reports `Guided` and never writes the live scheme.
- [ ] **Step 2: Verify RED**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Verify GREEN**

---

---

### Task 15: Startup entries

Inspection is implemented. Disabling and re-enabling are not, and the reason is a concrete constraint worth recording rather than rediscovering.

**The operation id cannot carry the entry name.** `ChangePlan.IsSafeCatalogOperationId` allows 96 lowercase characters, so after the `winora.startup.run.` prefix roughly 38 bytes remain. Measured on a real profile, Run entry names reach 56 bytes — `YandexBrowserAutoLaunch_EFB5B37C64649EA7404EBBBAEC96AF4B` and two more like it. Hex-encoding the name therefore does not fit, and a lossy slug cannot be reversed.

Since `IOperationFactory` must rebuild the operation from the id alone, the id has to be a hash of the entry name and the factory has to find the matching entry by scanning. That works while the entry exists. It does not work for an entry Winora has disabled, if disabling means deleting the value: nothing is left to scan, so the name needed to restore it is gone from the registry.

Two candidate resolutions, to decide before implementing:

1. **Winora-managed disabled store.** Move the value to a Winora-owned key instead of deleting it, mirroring what the specification already prescribes for Startup *folders* ("move a shortcut to/from a Winora-managed disabled directory"). Symmetric, the name is never lost, and a user can find and restore it by hand. Deviates from the current wording for Run entries, which says "disable by backed-up value removal".
2. **Keep deletion** and have the factory fall back to the durable journal and backup to recover the name. Matches the current wording but makes reconstruction depend on Winora's own state, which is exactly what the factory design set out to avoid.

Option 1 is the recommendation; it needs a specification amendment, not just code.

- [x] Inspect HKCU and HKLM Run entries read-only, with the source of each shown.
- [ ] Decide the disabled-entry representation and amend the specification.
- [ ] Implement disable and re-enable through the coordinator.

## Verification for every task

```powershell
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj -c Debug -p:Platform=x64
dotnet test tests/Winora.Architecture.Tests/Winora.Architecture.Tests.csproj -c Debug -p:Platform=x64
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' Winora.sln /restore /m /p:Configuration=Debug /p:Platform=x64
```

A domain is done only when its screen exists, every capability state is reachable in the UI with a real message, and the apply/verify/rollback path has been exercised by hand against a live setting with the change confirmed by a second tool.
