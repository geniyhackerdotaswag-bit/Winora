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
- The registry domains are unreliable until the package is signed. `RegistryWriteVirtualization` set to `disabled` needs the `unvirtualizedResources` restricted capability, which Windows does not grant to an unsigned package registered with `Add-AppxPackage -Register` — `SignatureKind` reports `None`. Measured symptom: a deleted value propagates to the real hive, but the container keeps a tombstone that masks it if something outside restores it, and a key the app creates itself is invisible from outside. Do not conclude a registry feature works from inside the app alone.
- Verify every new mutation mechanism from **outside** the app, in a separate unpackaged process, before calling it done. Independent verification inside Winora cannot detect a container that redirects the write and serves it back: MSIX registry virtualization did exactly that to the taskbar domain, and every layer reported success while the real user hive was untouched. `windows.registryWriteVirtualization` is disabled in the manifest for this reason — re-check it after any manifest change.
- No operation deletes user bytes in the step the user just confirmed. Reclamation moves into the Winora quarantine first; freeing the bytes is a separate retention decision. Never overstate `RollbackCapability` to make a feature look safer.
- Keep user-facing text in `.resw` resources, preserve keyboard/High Contrast/200% support, and do not add inert controls.

## Build

- Use .NET SDK 10.0.203, C# 14, `net10.0-windows10.0.26100.0`, Windows App SDK 2.2.0, and x64.
- Do not raise the `Microsoft.WindowsAppSDK` pin without first confirming a matching Dynamic Dependency Lifetime Manager package is installed. Unpackaged launches resolve the framework through the DDLM for their exact version; a 2.3.x pin on a machine that only has `DDLM.2.2.0.0` produces a process that starts and exits with no window, no log, and no managed exception.
- The unpackaged loop (`-p:WindowsPackageType=None`) and the packaged layout share `bin\x64\<Config>\`, and `Add-AppxPackage -Register` points at that directory. Rebuilding one flavour silently corrupts a registration made from the other; always clean between them.
- Run focused tests while developing, then build `Winora.sln` with MSBuild 18 in both Debug and Release before a release.
- Do not commit generated output, certificates, packages, `.superpowers/` scratch files, or secrets.
