# Architecture

Winora uses five production projects with one-way dependencies:

```text
Winora.Core
├── Winora.Infrastructure
├── Winora.System
├── Winora.ElevatedHost -> Winora.System + Winora.Infrastructure
└── Winora.App          -> Winora.System + Winora.Infrastructure
```

In project-reference terms, the enforced graph is:

| Project | Direct production references |
| --- | --- |
| `Winora.Core` | none |
| `Winora.Infrastructure` | `Winora.Core` |
| `Winora.System` | `Winora.Core` |
| `Winora.ElevatedHost` | `Winora.Core`, `Winora.System`, `Winora.Infrastructure` |
| `Winora.App` | `Winora.Core`, `Winora.Infrastructure`, `Winora.System` |

`Winora.Core` owns immutable change plans, capability and safety policy, contracts, and orchestration. It has no platform UI, serialization, registry, COM, P/Invoke, PowerShell, or concrete filesystem dependencies.

`Winora.Infrastructure` owns crash-safe Winora data: atomic JSON publication, backups, durable operation events, projections, journals, and the cross-process mutation lease. `Winora.System` implements Core contracts using documented Windows APIs and never displays UI.

`Winora.ElevatedHost` is a separate non-UI process for versioned, authenticated, allowlisted administrative operations. `Winora.App` is the medium-integrity WinUI process and the only presentation/composition layer.

Tests mirror the production layers. `Winora.Architecture.Tests` reads project files directly so dependency rules fail early without loading application code.

## Adding a capability domain

A new domain is one `IOperation` in `Winora.System/Operations/` plus one narrow adapter that carries its Microsoft Learn URI inline. It never introduces a second coordinator, a second plan type, or a second persistence path — `ChangeCoordinator`, `ChangeSafetyPolicy`, and the existing `Winora.Infrastructure` stores are the only ones. A domain that cannot be expressed this way is a signal that the mechanism is not documented well enough to ship as a direct mutation.

Every path under the Winora data directory is Infrastructure-owned. Operations address it through `WinoraDataPaths`, never by composing strings. Since 2026-08-04 the directory lives at `%USERPROFILE%\Winora`, not under `%LOCALAPPDATA%`: the packaged build's local app data is inside the package container, which Windows deletes on uninstall, and taking the journal and backups with it is the one loss recovery cannot survive.

Two domains sit outside `ChangeCoordinator` by explicit owner decision — temporary-file reclamation and the network bypass. Both are argued in the specification; neither is a precedent. A third requires the same written argument before it exists, not after.
