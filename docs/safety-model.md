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
