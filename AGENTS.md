# Winora contributor rules

## Architecture

- Preserve the production dependency graph: `Infrastructure -> Core`, `System -> Core`, `ElevatedHost -> Core + System + Infrastructure`, and `App -> Core + Infrastructure + System`.
- `Winora.Core` stays platform- and serializer-independent. It must not reference WinUI, Windows App SDK, System.Text.Json, the registry, PowerShell, COM, P/Invoke, or concrete file systems.
- `Winora.System` contains documented Windows adapters and never displays UI. `Winora.App` owns every dialog, notification, navigation, and confirmation surface.
- The main app stays medium integrity. Administrative work is allowlisted and isolated in `Winora.ElevatedHost`; never add arbitrary command execution.

## Safety and quality

- Develop behavior test-first. Every mutation must pass through capability probing, immutable dry-run planning, confirmation, verified backup, conditional apply, independent verification, durable journaling, and idempotent rollback.
- Block direct mutation for unknown, unsupported, partial, unavailable, or unsafe capabilities. Never replace external state after fingerprint drift.
- Use documented Windows mechanisms only and include the relevant Microsoft Learn URI next to every mutating adapter.
- A mechanism you cannot cite a Microsoft Learn URI for ships as `Guided` or `Unsupported` with a stable reason code. It never ships as an unconditional fallback, and a plausible-looking registry value is not documentation.
- Where a Learn page documents a value's *kind* differently from what the live system holds, probe both and degrade to `Unknown`/`Guided` on a mismatch instead of guessing.
- No operation deletes user bytes in the step the user just confirmed. Reclamation moves into the Winora quarantine first; freeing the bytes is a separate retention decision. Never overstate `RollbackCapability` to make a feature look safer.
- Keep user-facing text in `.resw` resources, preserve keyboard/High Contrast/200% support, and do not add inert controls.

## Build

- Use .NET SDK 10.0.203, C# 14, `net10.0-windows10.0.26100.0`, Windows App SDK 2.2.0, and x64.
- Run focused tests while developing, then build `Winora.sln` with MSBuild 18 in both Debug and Release before a release.
- Do not commit generated output, certificates, packages, `.superpowers/` scratch files, or secrets.
