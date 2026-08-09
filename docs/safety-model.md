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

`ChangeSafetyPolicy` refuses any plan that is not `Backup == Required` with `Rollback == Full`. Reclaiming disk space cannot satisfy that, and **the rule is not weakened to accommodate it.** Reclamation is instead kept entirely outside `ChangeCoordinator`.

An earlier revision of this document specified a two-step design — move into `%LOCALAPPDATA%\Winora\Quarantine\{operationId}` as a reversible forward apply, then purge as a separate retention decision — so that reclamation could pass the policy unchanged. **It was abandoned and never shipped.** Moving bytes to another folder on the same disk frees nothing, so the step that satisfied the policy would have reported reclaimed space that was still occupied. Buying a rollback capability at the price of a false report is the wrong trade in a project whose whole argument is that a verification must not lie.

What replaces it is narrower and honest about being irreversible:

1. **One location at a time, surveyed before the button is live.** File count and total bytes are shown first; nothing is deleted that Winora has not named.
2. **`TempReclamationPolicy` is the single authority** on what may be reclaimed, consulted by the probe, the screen and the cleaner. `WindowsTempCleaner.Clean` re-checks it at the call that deletes, because that call must not depend on a caller having asked correctly.
3. **Privilege is a fact about the process.** User-owned locations need nothing; the Windows-serviced ones are offered only to an elevated process, each stating its own cost. `Windows.old` is refused at every privilege level: no documented programmatic removal, and deleting it closes the route back to the previous Windows version for good.
4. **The report is of what was removed, not of what was asked for.** Open files are skipped and counted.

5. **Every reclamation is journalled**, successful or not, as a retention decision over a hashed location. Reclamation contributes no `IOperation`, so `JournalAllowlist` unions its identifiers into the journal allowlist explicitly: an identifier the journal rejects is dropped silently by design, which would turn a deletion into an act with no record at all.

`RollbackCapability` is never overstated to make a feature look safer. The Recycle Bin illustrates why: `FOF_ALLOWUNDO` is documented, but its undo is scoped to the Explorer session with no documented programmatic restore, so a Recycle Bin destination is at most `Partial` and can never be a direct-apply path.
