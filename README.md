# Winora

Winora is a Windows 11 customization app built around reversible, capability-aware changes. The app previews every supported mutation, records the expected source state, creates and verifies the required backup, applies conditionally, verifies independently, and retains an idempotent rollback path.

## Repository layout

- `src/Winora.Core` — pure plans, policies, contracts, and orchestration.
- `src/Winora.Infrastructure` — atomic persistence, backups, journals, and leases.
- `src/Winora.System` — documented Windows capability probes and adapters.
- `src/Winora.ElevatedHost` — non-UI allowlisted elevated operations.
- `src/Winora.App` — packaged WinUI 3 presentation and composition root.
- `tests` — layer, behavior, and architecture tests.

See [architecture](docs/architecture.md), [safety model](docs/safety-model.md), and [design system](docs/design-system.md) before changing cross-layer behavior.

## Prerequisites

- Windows 11 with the Windows 11 SDK 10.0.26100 or newer.
- .NET SDK 10.0.203.
- Visual Studio 18 with the C# WinUI tooling and MSBuild.

## Build and test

```powershell
dotnet test tests/Winora.Architecture.Tests/Winora.Architecture.Tests.csproj -c Debug
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' Winora.sln /restore /m /p:Configuration=Debug /p:Platform=x64
```

The initial scaffold only launches a blank Mica window. Feature behavior is introduced through later test-driven tasks.
