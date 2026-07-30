# Safety model

Winora treats every system customization as a recoverable operation, not a direct setting write.

## Required mutation pipeline

1. Probe support, risk, required rights, rollback coverage, restart needs, and the current fingerprint.
2. Produce an immutable dry-run plan and require explicit confirmation.
3. Revalidate the source fingerprint and acquire the global per-user mutation lease.
4. Create and verify the required backup; durably record `BackupCreated` before mutation.
5. Revalidate before every step, apply conditionally, and refuse to overwrite external drift.
6. Verify independently, durably journal the result, and retain an idempotent rollback path.

Direct mutation is blocked for `Unknown`, `Unsupported`, `Partial`, `NotAvailable`, and `UnsupportedForSafeMutation` capabilities. High-risk operations require the approved restore-point policy. A failed verification offers rollback; a failed rollback remains a recovery-required durable record.

The medium-integrity app never performs arbitrary elevated work. The elevated helper accepts only versioned, authenticated, same-account, allowlisted requests tied to the active lease and confirmed plan fingerprint. UAC cancellation leaves the verified local backup inspectable and produces no target mutation.

Durable transitions are published before any later system action. App startup reconciles incomplete journals at their last certain boundary; it never blindly replays an uncertain apply, rollback, or restore-point step. Logs and exported journal data are sanitized and omit secrets and full sensitive paths.

## Irreversible byte reclamation

`ChangeSafetyPolicy` refuses any plan that is not `Backup == Required` with `Rollback == Full`. Reclaiming disk space cannot satisfy that, and the rule is not weakened to accommodate it. Reclamation is therefore split in two:

1. **Quarantine — a reversible forward apply.** Items move into `%LOCALAPPDATA%\Winora\Quarantine\{operationId}` preserving their relative structure. The per-step backup is the item manifest (canonical path, length, last-write time, content hash); verification re-hashes at the destination; rollback moves the item back and reports `AlreadyRestored` when it is already home. This is honestly `RollbackCapability.Full` at `RiskLevel.Low` for a standard user and passes the existing policy unchanged. The probe requires a user-owned target on the same volume as `%LOCALAPPDATA%`, refuses protected and remote targets, and checks free space; a cross-volume target copies rather than renames, so it degrades to guided.
2. **Purge — a separate retention decision.** Freeing the bytes is never part of the step the user just confirmed. Because the bytes now live under `%LOCALAPPDATA%\Winora`, the purge is the retention decision section 7 of the specification already defines: one atomic decision over Winora-owned data, recorded in the action journal, never applied to a quarantine still linked to a recovery-required change. It does not pass through `ChangeCoordinator` and adds no exemption to the safety core.

`RollbackCapability` is never overstated to make a feature look safer. The Recycle Bin illustrates why: `FOF_ALLOWUNDO` is documented, but its undo is scoped to the Explorer session with no documented programmatic restore, so a Recycle Bin destination is at most `Partial` and can never be a direct-apply path.
