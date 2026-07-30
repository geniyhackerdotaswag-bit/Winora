# Winora MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the complete Winora Windows 11 customizer described by the approved design specification, including the WinUI shell, safe operation pipeline, atomic persistence, recovery, elevation, supported system operations, and all MVP screens.

**Architecture:** `Winora.Core` owns pure domain contracts and the coordinator; `Winora.Infrastructure` implements atomic JSON, durable journals, backup storage, and leases; `Winora.System` implements documented Windows operations; `Winora.ElevatedHost` is a separate allowlisted non-UI process; `Winora.App` is the only presentation layer. Direct mutations pass through dry-run, confirmation, backup, conditional mutation, verification, journal, and idempotent rollback.

**Tech Stack:** C# 14, .NET SDK 10.0.203, `net10.0-windows10.0.26100.0`, Windows App SDK 2.2.0, WinUI 3, CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions 10.0.9, System.Text.Json, xUnit 2.9.3.

## Global Constraints

- The approved source of truth is `docs/superpowers/specs/2026-07-12-winora-design.md`; architecture changes require user approval.
- `Winora.Core` references no WinUI, Windows App SDK, JSON serializer, registry, PowerShell, COM, P/Invoke, or concrete filesystem implementation.
- `Winora.System` implements Core contracts, uses only documented Windows mechanisms, and never displays UI.
- All dialogs, notifications, errors, navigation, and confirmation surfaces live in `Winora.App`; ViewModels call only interfaces.
- Every target mutation has an immutable plan, support/risk/rights/rollback/restart facts, verified backup, conditional mutation, independent verification, sanitized action event, and idempotent rollback.
- Direct mutation is blocked for `Unknown`, `Unsupported`, `Partial`, `NotAvailable`, or `UnsupportedForSafeMutation` capability results.
- JSON and durable transitions use same-directory staging, OS flush, readback/hash validation, write-through publication, and rebuildable projections.
- Apply, rollback, restore-point, and recovery actions require the global per-user mutation lease and durable transition journal.
- The main app stays medium integrity. Elevated operations support only same-account split-token consent and run through the versioned allowlisted helper.
- The UI uses NavigationView, Mica, the approved Fluent token grid, Russian `.resw` strings, Microsoft Fluent System Icons Regular 20, keyboard access, High Contrast, 200% scaling, and reduced motion.
- x64 Debug and Release must build with MSBuild 18; non-UI tests must pass with `dotnet test`; packaged launch is a release gate.

## Status as of 2026-07-30

The per-step checkboxes below were never ticked during implementation and no longer reflect reality. Actual state, verified by test run:

| Task | State | Notes |
|---|---|---|
| 1. Scaffold and boundaries | **Done** | `Winora.Architecture.Tests` now enforces all five documented dependency rules plus a ViewModel boundary rule (7 facts) |
| 2. Core plans, capability, state machine | **Done** | 246 tests |
| 3. Atomic JSON, backups, journals, lease | **Done and exceeded** | Also gained payload-capable backups (`IBackupCaptureProvider`, `BackupPayloadStore`), `IActionJournal` in Core, `IOperationRecoveryResolver`, retention machinery, and `WinoraStateRestorer` — none of which this plan anticipated. 277 tests |
| 4. Windows capabilities and adapters | **Partial (~20%)** | Only `VisualEffectsOperation` (two toggles), `OperationCapabilityPolicy`, `CapabilityBlockCodes`, `WindowsBuildProbe`. Missing: Run entries, Startup folders, folder/shortcut icons, sound and cursor preview services. 73 tests |
| 5. ElevatedHost, IPC, restore points | **Not started** | `Program.Main()` has an empty body. Blocks every High-risk domain — see below |
| 6. WinUI shell, tokens, DI, navigation | **Done** | NavigationView with the section 10 tree, token dictionaries, ru-RU `.resw`, one icon catalog, DI composition root, placeholder pages that state what is unbuilt. 56 tests |
| 7–10 | **Partial** | Themes is real and drives the full pipeline against live Windows; the remaining screens are placeholders. Packaging, accessibility audit, and release checks not started |

**Verified end to end on 2026-07-30** against the registered package: probe reads live `SystemParametersInfo` state, dry run produces the plan, confirmation applies it, the durable journal records `Planned → Prepared → BackupCreated → Applying → Applied → Verified → Completed`, the backup payload holds the literal previous value, and rollback restores the setting and reports success. Applying requires package identity because `GlobalMutationLease` demands it; unpackaged runs can probe, preview and review but not apply, and the review screen says so.

**Known blocker.** `src/Winora.Core/Changes/ChangeCoordinator.cs:102-116` blocks any plan with `RequiresRestorePoint` by delegating to a System Restore lifecycle coordinator that does not exist, while `src/Winora.Core/Changes/ChangeFacts.cs` blocks `Risk == High` without a restore point. Together these make every High-risk operation unimplementable until Task 5 supplies the coordinator. `OperationStatePolicy` already permits the needed transitions, so Task 5 is orchestration, not new state design.

New capability domains are planned separately in `2026-07-30-winora-capability-extension.md`.

---

### Task 1: Solution scaffold and enforced layer boundaries

**Files:**
- Create: `Winora.sln`
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `.editorconfig`
- Create: `.gitignore`
- Create: `AGENTS.md`
- Create: `README.md`
- Create: `docs/architecture.md`
- Create: `docs/design-system.md`
- Create: `docs/safety-model.md`
- Create: `src/Winora.Core/Winora.Core.csproj`
- Create: `src/Winora.Infrastructure/Winora.Infrastructure.csproj`
- Create: `src/Winora.System/Winora.System.csproj`
- Create: `src/Winora.ElevatedHost/Winora.ElevatedHost.csproj`
- Create: `src/Winora.ElevatedHost/Program.cs`
- Create: `src/Winora.ElevatedHost/app.manifest`
- Create: `src/Winora.App/Winora.App.csproj`
- Create: `src/Winora.App/App.xaml`
- Create: `src/Winora.App/App.xaml.cs`
- Create: `src/Winora.App/MainWindow.xaml`
- Create: `src/Winora.App/MainWindow.xaml.cs`
- Create: `src/Winora.App/Package.appxmanifest`
- Create: `src/Winora.App/app.manifest`
- Create: `src/Winora.App/Assets/*` from the installed Microsoft C# WinUI single-project MSIX template
- Create: `tests/Winora.Core.Tests/Winora.Core.Tests.csproj`
- Create: `tests/Winora.Infrastructure.Tests/Winora.Infrastructure.Tests.csproj`
- Create: `tests/Winora.System.Tests/Winora.System.Tests.csproj`
- Create: `tests/Winora.App.Tests/Winora.App.Tests.csproj`
- Create: `tests/Winora.Architecture.Tests/Winora.Architecture.Tests.csproj`
- Create: `tests/Winora.Architecture.Tests/SolutionStructureTests.cs`

**Interfaces:**
- Produces: the exact project graph `Infrastructure -> Core`, `System -> Core`, `ElevatedHost -> Core + System + Infrastructure`, `App -> Core + Infrastructure + System`.
- Produces: central package versions and common compiler/platform properties consumed by every later task.

- [ ] **Step 1: Add the failing architecture test before source projects exist**

```csharp
public sealed class SolutionStructureTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Core_has_no_outer_layer_or_platform_package_reference()
    {
        var xml = XDocument.Load(Path.Combine(Root, "src", "Winora.Core", "Winora.Core.csproj"));
        var refs = xml.Descendants().Where(x => x.Name.LocalName is "ProjectReference" or "PackageReference")
            .Select(x => (string?)x.Attribute("Include") ?? string.Empty).ToArray();
        Assert.DoesNotContain(refs, x => x.Contains("Winora.", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, x => x.Contains("WindowsAppSDK", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, x => x.Contains("System.Text.Json", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Winora.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
```

- [ ] **Step 2: Run the architecture test and verify RED**

Run: `dotnet test tests/Winora.Architecture.Tests/Winora.Architecture.Tests.csproj -c Debug`

Expected: FAIL because `src/Winora.Core/Winora.Core.csproj` does not exist.

- [ ] **Step 3: Create the solution and exact shared configuration**

```xml
<!-- Directory.Build.props -->
<Project>
  <PropertyGroup>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest</AnalysisLevel>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.22000.0</SupportedOSPlatformVersion>
    <TargetPlatformMinVersion>10.0.22000.0</TargetPlatformMinVersion>
    <Platforms>x64</Platforms>
    <RuntimeIdentifiers>win-x64</RuntimeIdentifiers>
  </PropertyGroup>
</Project>
```

```xml
<!-- Directory.Packages.props -->
<Project>
  <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.WindowsAppSDK" Version="2.2.0" />
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.9" />
    <PackageVersion Include="Microsoft.Extensions.Logging" Version="10.0.9" />
    <PackageVersion Include="Microsoft.Extensions.Options" Version="10.0.9" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.7.0" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageVersion Include="coverlet.collector" Version="10.0.1" />
  </ItemGroup>
</Project>
```

```json
{
  "sdk": {
    "version": "10.0.203",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

- [ ] **Step 4: Create the five production projects, five test projects, references, docs, README, and AGENTS rules**

Use SDK-style projects. `Winora.App` sets `OutputType=WinExe`, `UseWinUI=true`, `EnableMsixTooling=true` and includes the minimal template-derived `App`/`MainWindow`/package manifest/assets needed to compile and package; `Winora.ElevatedHost` sets `OutputType=WinExe`, `ApplicationManifest=app.manifest`, and returns immediately from a minimal `Program.Main` until Task 5 replaces it through TDD. All other production projects are class libraries with one namespace marker. Add every project to `Winora.sln` with explicit solution folders `src` and `tests`.

- [ ] **Step 5: Run RED-to-GREEN verification**

Run: `dotnet test tests/Winora.Architecture.Tests/Winora.Architecture.Tests.csproj -c Debug`

Expected: PASS, 0 warnings and 0 errors.

Run: `& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' Winora.sln /restore /m /p:Configuration=Debug /p:Platform=x64`

Expected: build succeeds for the scaffold.

- [ ] **Step 6: Commit**

```powershell
git add Winora.sln global.json Directory.Build.props Directory.Packages.props .editorconfig .gitignore AGENTS.md README.md docs src tests
git commit -m "build: scaffold Winora architecture"
```

---

### Task 2: Core plans, capability model, and durable state machine

**Files:**
- Create: `src/Winora.Core/Changes/ChangePlan.cs`
- Create: `src/Winora.Core/Changes/ChangeStep.cs`
- Create: `src/Winora.Core/Changes/ChangeFacts.cs`
- Create: `src/Winora.Core/Changes/OperationState.cs`
- Create: `src/Winora.Core/Changes/ChangeCoordinator.cs`
- Create: `src/Winora.Core/Contracts/IOperation.cs`
- Create: `src/Winora.Core/Contracts/IBackupRepository.cs`
- Create: `src/Winora.Core/Contracts/IDurableOperationJournal.cs`
- Create: `src/Winora.Core/Contracts/IMutationLease.cs`
- Create: `src/Winora.Core/Contracts/IClock.cs`
- Create: `tests/Winora.Core.Tests/Changes/ChangePlanTests.cs`
- Create: `tests/Winora.Core.Tests/Changes/ChangeCoordinatorTests.cs`

**Interfaces:**
- Produces: `IOperation.ProbeAsync`, `PreviewAsync`, `ApplyStepAsync`, `VerifyStepAsync`, `RollbackStepAsync`.
- Produces: immutable plan digest/fingerprint contracts consumed by Infrastructure, System, ElevatedHost, and App.

- [ ] **Step 1: Write failing digest and transition tests**

```csharp
[Fact]
public void Equivalent_plans_have_the_same_digest() =>
    Assert.Equal(PlanFixture.Create().Digest, PlanFixture.Create().Digest);

[Theory]
[InlineData(OperationState.Planned, OperationState.Prepared)]
[InlineData(OperationState.Prepared, OperationState.BackupCreated)]
[InlineData(OperationState.BackupCreated, OperationState.Applying)]
public void Allowed_transition_is_accepted(OperationState from, OperationState to) =>
    Assert.True(OperationStatePolicy.CanTransition(from, to));

[Fact]
public void Applying_cannot_skip_to_completed() =>
    Assert.False(OperationStatePolicy.CanTransition(OperationState.Applying, OperationState.Completed));
```

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/Winora.Core.Tests/Winora.Core.Tests.csproj -c Debug --filter "ChangePlanTests|ChangeCoordinatorTests"`

Expected: compilation fails because the Core types do not exist.

- [ ] **Step 3: Implement immutable records, canonical SHA-256 digest, blocking policy, and state transitions**

```csharp
public sealed record ChangePlan(
    Guid PlanId,
    string OperationId,
    string Title,
    IReadOnlyList<ChangeStep> Steps,
    RiskLevel Risk,
    PrivilegeRequirement Privilege,
    RollbackCapability Rollback,
    RestartRequirement Restart,
    SupportStatus Support,
    StateFingerprint SourceFingerprint,
    Uri Documentation,
    BackupRequirement Backup,
    bool RequiresRestorePoint,
    string Digest);

public interface IOperation
{
    string OperationId { get; }
    ValueTask<OperationCapability> ProbeAsync(OperationTarget target, CancellationToken cancellationToken);
    ValueTask<ChangePlan> PreviewAsync(OperationDraft draft, CancellationToken cancellationToken);
    ValueTask<StepResult> ApplyStepAsync(ChangePlan plan, ChangeStep step, CancellationToken cancellationToken);
    ValueTask<VerificationResult> VerifyStepAsync(ChangePlan plan, ChangeStep step, CancellationToken cancellationToken);
    ValueTask<StepResult> RollbackStepAsync(RollbackPlan plan, ChangeStep step, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Add coordinator tests for drift, cancellation, partial apply, verification failure, and idempotent rollback; then implement minimal orchestration**

Run each new test once before implementation and confirm the expected failure. The coordinator must never call `ApplyStepAsync` until `BackupCreated` is durable and must not advance past a non-durable transition.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test tests/Winora.Core.Tests/Winora.Core.Tests.csproj -c Debug`

Expected: all Core tests pass.

```powershell
git add src/Winora.Core tests/Winora.Core.Tests
git commit -m "feat(core): add safe change state machine"
```

---

### Task 3: Atomic JSON, backups, durable journals, and mutation lease

**Files:**
- Create: `src/Winora.Infrastructure/Persistence/AtomicJsonFile.cs`
- Create: `src/Winora.Infrastructure/Persistence/WriteThroughPublisher.cs`
- Create: `src/Winora.Infrastructure/Persistence/JsonDocumentEnvelope.cs`
- Create: `src/Winora.Infrastructure/Operations/DurableOperationJournal.cs`
- Create: `src/Winora.Infrastructure/Operations/OperationProjection.cs`
- Create: `src/Winora.Infrastructure/Backups/BackupRepository.cs`
- Create: `src/Winora.Infrastructure/Backups/WinoraStateBackupService.cs`
- Create: `src/Winora.Infrastructure/Journal/ActionJournal.cs`
- Create: `src/Winora.Infrastructure/Leases/GlobalMutationLease.cs`
- Create: `src/Winora.Infrastructure/Paths/WinoraDataPaths.cs`
- Create: `tests/Winora.Infrastructure.Tests/Persistence/AtomicJsonFileTests.cs`
- Create: `tests/Winora.Infrastructure.Tests/Operations/DurableOperationJournalTests.cs`
- Create: `tests/Winora.Infrastructure.Tests/Leases/GlobalMutationLeaseTests.cs`

**Interfaces:**
- Implements: Core `IBackupRepository`, `IDurableOperationJournal`, `IMutationLease`, manual Winora-state backup/verify/restore, and sanitized action-journal contracts.
- Produces: `%LOCALAPPDATA%\Winora` layout and deterministic test-root injection.

- [ ] **Step 1: Write failing tests for interrupted writes, concurrent revisions, hash corruption, and abandoned lease recovery**

```csharp
[Fact]
public async Task Interrupted_publish_keeps_last_known_good_document()
{
    var publisher = Fixture.WithFault(FaultPoint.BeforeWriteThroughMove);
    await Assert.ThrowsAsync<InjectedCrashException>(() => publisher.WriteAsync("state.json", new { value = 2 }));
    Assert.Equal(1, (await Fixture.ReadAsync<ValueDocument>("state.json")).Value);
}

[Fact]
public async Task Two_processes_cannot_hold_the_same_mutation_lease() =>
    Assert.False((await Fixture.SecondProcess.TryAcquireAsync()).Acquired);
```

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/Winora.Infrastructure.Tests/Winora.Infrastructure.Tests.csproj -c Debug`

Expected: compilation fails because Infrastructure implementations do not exist.

- [ ] **Step 3: Implement same-directory temp writes, `Flush(true)`, readback/hash, `MoveFileExW(MOVEFILE_WRITE_THROUGH)`, committed backup markers, immutable transition chain, and rebuildable projections**

```csharp
public interface IWriteThroughPublisher
{
    ValueTask PublishNewAsync(string temporaryPath, string finalPath, CancellationToken cancellationToken);
    ValueTask ReplaceProjectionAsync(string temporaryPath, string finalPath, string lastKnownGoodPath, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement global lease epochs and validated App/helper owner sets**

Use `Global\Winora.Mutation.{UserSidHash}`, an ACL limited to the user SID and SYSTEM, PID plus process-start-time validation, durable heartbeat/epoch records, and no takeover while a surviving helper owner exists.

- [ ] **Step 5: Add crash matrix tests and verify GREEN**

Run: `dotnet test tests/Winora.Infrastructure.Tests/Winora.Infrastructure.Tests.csproj -c Debug`

Expected: all Infrastructure tests pass with no warnings.

- [ ] **Step 6: Commit**

```powershell
git add src/Winora.Infrastructure tests/Winora.Infrastructure.Tests
git commit -m "feat(storage): add crash-safe operation persistence"
```

---

### Task 4: Documented Windows capabilities and direct operation adapters

**Files:**
- Create: `src/Winora.System/Windows/WindowsBuildProbe.cs`
- Create: `src/Winora.System/Operations/VisualEffectsOperation.cs`
- Create: `src/Winora.System/Operations/RunEntryOperation.cs`
- Create: `src/Winora.System/Operations/StartupFolderOperation.cs`
- Create: `src/Winora.System/Operations/FolderIconOperation.cs`
- Create: `src/Winora.System/Operations/ShortcutIconOperation.cs`
- Create: `src/Winora.System/Operations/GuidedSettingsOperation.cs`
- Create: `src/Winora.System/Operations/SoundPreviewService.cs`
- Create: `src/Winora.System/Operations/CursorPreviewService.cs`
- Create: `src/Winora.System/Safety/IConditionalSystemMutation.cs`
- Create: `tests/Winora.System.Tests/Operations/OperationCapabilityTests.cs`
- Create: `tests/Winora.System.Tests/Operations/ConditionalMutationRaceTests.cs`
- Create: `tests/Winora.System.Tests/Operations/IconRoundTripTests.cs`

**Interfaces:**
- Implements: Core `IOperation` registrations for SPI effects, HKCU/HKLM Run, user/common Startup folder, folder icon, shortcut icon, and guided settings.
- Produces: typed read-only sound/cursor/theme status and preview services.

- [ ] **Step 1: Write failing capability/race/round-trip tests**

```csharp
[Fact]
public async Task External_change_after_expected_read_is_not_overwritten()
{
    var result = await Fixture.RunEntry.TryApplyAsync(Fixture.Plan, race: Fixture.ExternalWriter);
    Assert.Equal(StepStatus.ExternalDrift, result.Status);
    Assert.Equal("external-value", Fixture.Registry.CurrentValue);
}
```

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj -c Debug`

Expected: missing System operation types.

- [ ] **Step 3: Implement read-only probes and documented direct adapters behind narrow native interfaces**

Every source file that mutates Windows includes the exact Microsoft Learn URI. Protected/read-only/remote targets fail with typed capability reasons. No AppEvents, StartupApproved, cursor/theme persistence, or undocumented DWM/Explorer mutations are added.

- [ ] **Step 4: Implement conditional mutation or return `UnsupportedForSafeMutation`**

```csharp
public interface IConditionalSystemMutation
{
    ValueTask<ConditionalMutationResult> TryApplyAsync(
        OperationTarget target,
        StateFingerprint expected,
        ReadOnlyMemory<byte> proposed,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 5: Verify and commit**

Run: `dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj -c Debug`

```powershell
git add src/Winora.System tests/Winora.System.Tests
git commit -m "feat(system): add documented customization operations"
```

---

### Task 5: ElevatedHost, versioned IPC, and System Restore lifecycle

**Files:**
- Create: `src/Winora.ElevatedHost/app.manifest`
- Create: `src/Winora.ElevatedHost/Program.cs`
- Create: `src/Winora.ElevatedHost/Ipc/IpcProtocol.cs`
- Create: `src/Winora.ElevatedHost/Ipc/CallerIdentityValidator.cs`
- Create: `src/Winora.ElevatedHost/Ipc/AllowlistedOperationDispatcher.cs`
- Create: `src/Winora.System/Restore/SystemRestoreService.cs`
- Create: `src/Winora.System/Restore/SystemRestoreInventory.cs`
- Create: `tests/Winora.System.Tests/Elevation/IpcProtocolTests.cs`
- Create: `tests/Winora.System.Tests/Restore/SystemRestoreLifecycleTests.cs`

**Interfaces:**
- Consumes: Core plans/journal/lease and System allowlisted handlers.
- Produces: HMAC-authenticated request/result envelopes and durable restore-point transitions.

- [ ] **Step 1: Write failing tests for protocol version, nonce replay, caller mismatch, UAC cancellation result, allowlist rejection, BEGIN crash, reused sequence, and ambiguous END**

```csharp
[Fact]
public async Task Unknown_operation_is_rejected_without_dispatch() =>
    Assert.Equal(IpcStatus.RejectedOperation, (await Fixture.SendAsync("shell.execute")).Status);

[Fact]
public async Task Crash_after_end_request_does_not_repeat_end_blindly() =>
    Assert.Equal(RestorePointState.FinalizeOutcomeUnknown, await Fixture.RecoverAmbiguousEndAsync());
```

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj -c Debug --filter "IpcProtocolTests|SystemRestoreLifecycleTests"`

- [ ] **Step 3: Implement the no-UI helper and protocol 1.0**

The command line contains only protocol version, correlation ID, and pipe locator. Validate same SID/session, package family, signed executable path, nonce/expiry, lease membership, plan digest, payload schema, support, and fingerprint after elevation. Dispatch only compiled operation IDs.

- [ ] **Step 4: Implement `BEGIN_SYSTEM_CHANGE`/ownership/`END_SYSTEM_CHANGE`/CANCEL and recovery states**

Persist `RestorePointBeginRequested`, `RestorePointBeginReturnedUnverified`, ownership proof, `RestorePointBegun`, request/final states, sequence number, and unknown-finalization recovery exactly as the specification defines.

- [ ] **Step 5: Verify, build helper, and commit**

Run: `dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj -c Debug`

Run: `dotnet build src/Winora.ElevatedHost/Winora.ElevatedHost.csproj -c Debug -p:Platform=x64`

```powershell
git add src/Winora.ElevatedHost src/Winora.System/Restore tests/Winora.System.Tests
git commit -m "feat(elevation): add allowlisted elevated host"
```

---

### Task 6: WinUI application shell, design tokens, DI, and navigation

**Files:**
- Modify: `src/Winora.App/App.xaml`
- Modify: `src/Winora.App/App.xaml.cs`
- Modify: `src/Winora.App/MainWindow.xaml`
- Modify: `src/Winora.App/MainWindow.xaml.cs`
- Modify: `src/Winora.App/Package.appxmanifest`
- Modify: `src/Winora.App/app.manifest`
- Create: `src/Winora.App/Resources/Styles/DesignTokens.xaml`
- Create: `src/Winora.App/Resources/Styles/Controls.xaml`
- Create: `src/Winora.App/Resources/Strings/ru-RU/Resources.resw`
- Create: `src/Winora.App/Assets/Icons/THIRD-PARTY-NOTICES.md`
- Create: `src/Winora.App/Navigation/NavigationService.cs`
- Create: `src/Winora.App/Navigation/RouteRegistry.cs`
- Create: `src/Winora.App/Services/ServiceRegistration.cs`
- Create: `tests/Winora.App.Tests/Navigation/RouteRegistryTests.cs`
- Create: `tests/Winora.App.Tests/Architecture/ViewModelBoundaryTests.cs`

**Interfaces:**
- Produces: `INavigationService`, `IDialogService`, `INotificationService`, `IWindowService`, `IFilePickerService`, route/page factory.
- Consumes: Core interfaces through DI; no ViewModel receives Infrastructure/System concrete types.

- [ ] **Step 1: Write failing route and ViewModel-boundary tests**

Require routes for Dashboard, Themes, Sounds, Cursors, Icons, Startup, Changes, Backups, Journal, Settings, Compatibility, review, applying, results, and recovery.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/Winora.App.Tests/Winora.App.Tests.csproj -c Debug`

- [ ] **Step 3: Implement package manifest, Mica window, NavigationView, DI composition root, route registry, and all placeholder pages**

```xml
<NavigationView x:Name="Navigation" PaneDisplayMode="LeftCompact"
                IsSettingsVisible="False" IsBackButtonVisible="Collapsed">
  <Frame x:Name="ContentFrame" />
</NavigationView>
```

Each placeholder must navigate and display an explicit supported/guided/in-development message; no dead controls are allowed.

- [ ] **Step 4: Implement the shared 4/8 px token dictionaries and interaction states**

Use only the spacing/radius/icon sizes from the specification, Mica on `Window.SystemBackdrop`, Acrylic only for transient overlays, and official Fluent Regular 20 SVG assets through one presenter.

- [ ] **Step 5: Verify x64 Debug/Release and commit**

Run: `dotnet test tests/Winora.App.Tests/Winora.App.Tests.csproj -c Debug`

Run: `& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' Winora.sln /restore /m /p:Configuration=Debug /p:Platform=x64`

Run: `& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' Winora.sln /m /p:Configuration=Release /p:Platform=x64`

```powershell
git add src/Winora.App tests/Winora.App.Tests
git commit -m "feat(app): add Fluent navigation shell"
```

---

### Task 7: Dashboard and guided personalization screens

**Files:**
- Create: `src/Winora.App/Views/DashboardPage.xaml`
- Create: `src/Winora.App/ViewModels/DashboardViewModel.cs`
- Create: `src/Winora.App/Views/ThemesPage.xaml`
- Create: `src/Winora.App/ViewModels/ThemesViewModel.cs`
- Create: `src/Winora.App/Views/SoundsPage.xaml`
- Create: `src/Winora.App/ViewModels/SoundsViewModel.cs`
- Create: `src/Winora.App/Views/CursorsPage.xaml`
- Create: `src/Winora.App/ViewModels/CursorsViewModel.cs`
- Create: `src/Winora.App/Views/CompatibilityPage.xaml`
- Create: `src/Winora.App/ViewModels/CompatibilityViewModel.cs`
- Create: `src/Winora.App/Views/SettingsPage.xaml`
- Create: `src/Winora.App/ViewModels/SettingsViewModel.cs`
- Create: `docs/design-reviews/stage-7-dashboard-guided-pages.md`
- Create: `tests/Winora.App.Tests/ViewModels/DashboardViewModelTests.cs`
- Create: `tests/Winora.App.Tests/ViewModels/GuidedPageTests.cs`

**Interfaces:**
- Consumes: capability/read models, navigation, file picker, preview services.
- Produces: fully clickable dashboard cards/activity rows and guided Windows-owned routes.

- [ ] **Step 1: Write failing command/route tests for every visible interaction**

Test quick cards, backup command, refresh, help, compatibility details, activity rows, Windows Colors, Sound control panel, Mouse settings, WAV preview, cursor preview, Winora theme selection, motion preference, backup retention, log retention, and diagnostics export.

- [ ] **Step 2: Verify RED, implement ViewModels, then verify GREEN**

Run: `dotnet test tests/Winora.App.Tests/Winora.App.Tests.csproj -c Debug --filter "DashboardViewModelTests|GuidedPageTests"`

- [ ] **Step 3: Implement approved card hierarchy and responsive pages**

Preserve the Dashboard protection/Windows/quick-access/recent-action layout, 20 px icons, hover/pressed/focus/disabled states, WCAG AA, reduced motion, and no decorative fake actions.

- [ ] **Step 4: Run interaction audit and commit**

For Dashboard, Themes, Sounds, Cursors, Compatibility, and Settings, record problems found, fixes made, and elements intentionally unchanged with reasons in the stage design-review document before declaring the screen complete.

Run: `dotnet test tests/Winora.App.Tests/Winora.App.Tests.csproj -c Debug`

```powershell
git add src/Winora.App tests/Winora.App.Tests
git commit -m "feat(ui): add dashboard and guided personalization"
```

---

### Task 8: Startup, icons, drafts, dry-run, and confirmation UX

**Files:**
- Create: `src/Winora.App/Views/StartupPage.xaml`
- Create: `src/Winora.App/ViewModels/StartupViewModel.cs`
- Create: `src/Winora.App/Views/IconsPage.xaml`
- Create: `src/Winora.App/ViewModels/IconsViewModel.cs`
- Create: `src/Winora.App/Views/ChangeReviewPage.xaml`
- Create: `src/Winora.App/ViewModels/ChangeReviewViewModel.cs`
- Create: `src/Winora.App/Views/ApplyingPage.xaml`
- Create: `src/Winora.App/ViewModels/ApplyingViewModel.cs`
- Create: `tests/Winora.App.Tests/ViewModels/DraftSafetyTests.cs`
- Create: `tests/Winora.App.Tests/ViewModels/ChangeReviewViewModelTests.cs`
- Create: `docs/design-reviews/stage-8-direct-operation-pages.md`

**Interfaces:**
- Consumes: Core coordinator and operation catalog through interfaces.
- Produces: local drafts, exact diff review, confirmation token, apply progress, and cancellation boundary.

- [ ] **Step 1: Write failing tests proving toggles edit drafts only and cancellation performs no operation call**

```csharp
[Fact]
public async Task Startup_toggle_only_changes_draft_until_preview_and_confirm()
{
    await ViewModel.ToggleAsync(Item);
    Assert.Empty(Operation.ApplyCalls);
    Assert.True(ViewModel.HasDraft);
}
```

- [ ] **Step 2: Verify RED, implement ViewModels/pages, verify GREEN**

Review copy must contain exact current/proposed values, target, risk, rights, backup, rollback, support, restart, and restore-point facts.

- [ ] **Step 3: Add file/folder validation and preview for folder/shortcut icons**

Block protected, remote, read-only, unsupported targets with precise localized reasons. Managed asset copy occurs only as part of confirmed apply/backup preparation.

- [ ] **Step 4: Verify and commit**

Record the three-part visual review for Startup, Icons, Change Review, and Applying before commit.

Run: `dotnet test tests/Winora.App.Tests/Winora.App.Tests.csproj -c Debug --filter "DraftSafetyTests|ChangeReviewViewModelTests"`

```powershell
git add src/Winora.App tests/Winora.App.Tests
git commit -m "feat(ui): add safe drafts and change review"
```

---

### Task 9: Changes, backups, journal, rollback, and startup recovery UX

**Files:**
- Create: `src/Winora.App/Views/ChangesPage.xaml`
- Create: `src/Winora.App/ViewModels/ChangesViewModel.cs`
- Create: `src/Winora.App/Views/BackupsPage.xaml`
- Create: `src/Winora.App/ViewModels/BackupsViewModel.cs`
- Create: `src/Winora.App/Views/JournalPage.xaml`
- Create: `src/Winora.App/ViewModels/JournalViewModel.cs`
- Create: `src/Winora.App/Views/RollbackReviewPage.xaml`
- Create: `src/Winora.App/ViewModels/RollbackReviewViewModel.cs`
- Create: `src/Winora.App/Views/RecoveryPage.xaml`
- Create: `src/Winora.App/ViewModels/RecoveryViewModel.cs`
- Create: `tests/Winora.App.Tests/Recovery/RecoveryViewModelTests.cs`
- Create: `tests/Winora.Core.Tests/Recovery/CrashRecoveryTests.cs`
- Create: `docs/design-reviews/stage-9-recovery-pages.md`

**Interfaces:**
- Consumes: durable journals, backup repository, coordinator, restore lifecycle, notification/navigation services.
- Produces: filterable history, backup verification, sanitized export, conflict review, recovery choices, and idempotent rollback.

- [ ] **Step 1: Write failing recovery tests for all required crash boundaries**

Cover two instances, mutation-before-result crash, between-step crash, drift after dry-run/UAC, UAC cancel, crash after BEGIN, repeated rollback, and restart recovery.

- [ ] **Step 2: Verify RED and implement recovery projections/ViewModels**

Never replay `Applying`/`RollingBack`. Reconcile exact proposed/backup/third states and show only verify/complete, rollback, conflict review, restore finalization, or no-change cancellation when valid.

- [ ] **Step 3: Implement fully clickable changes/backups/journal rows and sanitized JSONL export**

Journal export excludes secrets, raw exceptions, full paths, tokens, usernames, registry dumps, and command lines.

- [ ] **Step 4: Verify and commit**

Record the three-part visual review for Changes, Backups, Journal, Rollback Review, and Recovery before commit.

Run: `dotnet test tests/Winora.Core.Tests/Winora.Core.Tests.csproj -c Debug`

Run: `dotnet test tests/Winora.App.Tests/Winora.App.Tests.csproj -c Debug`

```powershell
git add src/Winora.App tests/Winora.App.Tests tests/Winora.Core.Tests
git commit -m "feat(recovery): add history rollback and recovery UX"
```

---

### Task 10: Packaging, interaction audit, accessibility, and release verification

**Files:**
- Modify: `src/Winora.App/Package.appxmanifest`
- Modify: `src/Winora.App/Winora.App.csproj`
- Modify: `src/Winora.ElevatedHost/Winora.ElevatedHost.csproj`
- Create: `build/Verify-InteractionContracts.ps1`
- Create: `build/Verify-Package.ps1`
- Create: `tests/Winora.App.Tests/Interaction/InteractionContractTests.cs`
- Create: `tests/Winora.System.Tests/Integration/SupportedOperationSmokeTests.cs`
- Modify: `README.md`

**Interfaces:**
- Produces: signed-package configuration, `runFullTrust`/`allowElevation`, packaged helper content, release scripts, and complete operator documentation.

- [ ] **Step 1: Write failing interaction/package contract tests**

Assert every NavigationView item, card, CommandBar item, row, confirmation/error action, and placeholder has a route/command/help contract. Assert package manifest contains the two restricted capabilities and the helper executable is packaged separately.

- [ ] **Step 2: Verify RED, complete package configuration and audit scripts, verify GREEN**

The package uses the approved sideload/enterprise channel. Development certificates remain untracked; no private key is committed.

- [ ] **Step 3: Run the complete automated suite**

Run: `dotnet test Winora.sln -c Debug -p:Platform=x64`

Expected: all tests pass, 0 warnings and 0 errors.

- [ ] **Step 4: Build Debug and Release packages**

Run: `& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' Winora.sln /restore /m /p:Configuration=Debug /p:Platform=x64`

Run: `& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' Winora.sln /m /p:Configuration=Release /p:Platform=x64`

Expected: both builds succeed with 0 warnings and 0 errors.

- [ ] **Step 5: Perform packaged launch and manual release checks**

Run the package on Windows 11, navigate every route, execute dry-run/cancel, a standard-user reversible operation, rollback twice, guided settings routes, recovery fixture, keyboard navigation, Narrator names, High Contrast, 200% scaling, and reduced motion. Record results in `README.md` release verification.

- [ ] **Step 6: Commit**

```powershell
git add src tests build README.md
git commit -m "test: complete Winora MVP release gates"
```

---

## Plan self-review checklist

- Every MVP screen and system-operation boundary in the approved specification maps to a task above.
- Core, Infrastructure, System, ElevatedHost, and App dependencies remain directional and test-enforced.
- The full safety sequence, atomic JSON, lease, TOCTOU protection, restore lifecycle, and recovery matrix have dedicated implementation and tests.
- No direct undocumented theme/transparency/sound/cursor mutation or PowerShell path is introduced.
- Every task has a RED test, expected failure, implementation boundary, GREEN verification, and commit.
- Stage reporting occurs after Tasks 1, 3, 5, 7, 9, and 10, with the next task named explicitly.
