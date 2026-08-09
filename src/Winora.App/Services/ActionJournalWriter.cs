using System.Security.Cryptography;
using System.Text;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Core.Journal;

namespace Winora.App.Services;

/// <summary>Records what Winora did, in the sanitized form the audit trail requires.</summary>
public interface IActionJournalWriter
{
    Task RecordApplyAsync(
        ChangePlan plan,
        CoordinatorDisposition disposition,
        CancellationToken cancellationToken = default);

    Task RecordRollbackAsync(
        ChangePlan plan,
        bool succeeded,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records one temporary-file reclamation. It carries no <see cref="ChangePlan" /> because it is
    /// not a coordinator operation, which is precisely why it needs its own entry point: without
    /// one, deleting the user's bytes is the only thing Winora does that leaves no trace anywhere.
    /// </summary>
    /// <param name="deletedCount">Files removed, or null when the attempt failed part-way.</param>
    Task RecordReclamationAsync(
        string locationId,
        string locationPath,
        bool requiredElevation,
        bool succeeded,
        int? deletedCount,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The audit trail, finally written.
/// </summary>
/// <remarks>
/// <para>
/// <c>ActionJournal</c> existed, was tested, and was registered — and nothing ever called it, so the
/// journal was empty on every machine. That gap was recorded in the plan as far back as
/// 2026-07-30 and outlived several releases. A screen over an always-empty journal would have been
/// a feature that lies, so the writing comes first.
/// </para>
/// <para>
/// Entries carry no paths and no values, by specification: category, outcome, risk, privilege and a
/// correlation hash of the target. That is deliberate — the trail has to be safe to share when
/// something goes wrong, and a log that leaks what the user changed would not be.
/// </para>
/// <para>
/// A failure to journal never fails the change. The change has already happened by then; refusing to
/// report it would not undo it, and throwing here would turn a completed change into an apparent
/// error.
/// </para>
/// </remarks>
public sealed class ActionJournalWriter : IActionJournalWriter
{
    private readonly IActionJournal _journal;

    public ActionJournalWriter(IActionJournal journal)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public Task RecordApplyAsync(
        ChangePlan plan,
        CoordinatorDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return AppendAsync(plan, StatusFor(disposition), cancellationToken);
    }

    public Task RecordRollbackAsync(
        ChangePlan plan,
        bool succeeded,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return AppendAsync(
            plan,
            succeeded ? ActionJournalStatus.RolledBack : ActionJournalStatus.RollbackFailed,
            cancellationToken);
    }

    /// <summary>The stable catalog identifier a reclamation of <paramref name="locationId" /> uses.</summary>
    /// <remarks>
    /// Public because the composition root builds the journal allowlist from it. The two must agree,
    /// and one function they both call is the only way to guarantee that.
    /// </remarks>
    public static string ReclamationOperationId(string locationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return "winora.cleanup." + locationId;
    }

    public async Task RecordReclamationAsync(
        string locationId,
        string locationPath,
        bool requiredElevation,
        bool succeeded,
        int? deletedCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(locationPath);

        try
        {
            var draft = new ActionJournalEntryDraft(
                Guid.NewGuid(),
                ReclamationOperationId(locationId),
                ActionJournalEventKind.RetentionDecision,
                ActionJournalCategory.Retention,
                succeeded
                    ? ActionJournalStatus.RetentionCompleted
                    : ActionJournalStatus.RetentionFailed,
                // Not Low for the Windows-serviced locations. Clearing SoftwareDistribution costs the
                // ability to roll an update back, and if that is worth a warning on screen it is
                // worth being visible in the trail someone reads afterwards.
                requiredElevation ? ActionJournalRisk.Medium : ActionJournalRisk.Low,
                requiredElevation
                    ? ActionJournalPrivilege.Administrator
                    : ActionJournalPrivilege.StandardUser,
                ActionJournalSupportStatus.Supported,
                Guid.NewGuid(),
                CorrelationHashOf(locationPath),
                deletedCount);

            await _journal.AppendAsync(draft, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately swallowed: see the class remarks.
        }
    }

    /// <remarks>
    /// The path is hashed, never stored. Two entries about the same location correlate; neither
    /// reveals where on disk the user's files were. Case-folded first because Windows paths are
    /// case-insensitive and the same location must not produce two different hashes.
    /// </remarks>
    private static string CorrelationHashOf(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));

    private async Task AppendAsync(
        ChangePlan plan,
        ActionJournalStatus status,
        CancellationToken cancellationToken)
    {
        try
        {
            var draft = new ActionJournalEntryDraft(
                plan.PlanId,
                plan.OperationId,
                ActionJournalEventKind.Operation,
                CategoryFor(plan.OperationId),
                status,
                RiskFor(plan.Risk),
                plan.Privilege == PrivilegeRequirement.Administrator
                    ? ActionJournalPrivilege.Administrator
                    : ActionJournalPrivilege.StandardUser,
                SupportFor(plan.Support),
                Guid.NewGuid(),
                // The step target, hashed. Enough to correlate two entries about the same thing,
                // never enough to reveal what it was.
                plan.Steps.Count > 0 ? plan.Steps[0].Target.TargetId : null,
                plan.Steps.Count);

            await _journal.AppendAsync(draft, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately swallowed: see the class remarks.
        }
    }

    /// <remarks>
    /// Derived from the catalog identifier, which is a stable slug the operations own. Anything
    /// unrecognised lands in <see cref="ActionJournalCategory.Application" /> rather than being
    /// guessed into a domain it may not belong to.
    /// </remarks>
    private static ActionJournalCategory CategoryFor(string operationId) => operationId switch
    {
        var id when id.StartsWith("winora.visual-effects.", StringComparison.Ordinal) =>
            ActionJournalCategory.WindowsPersonalization,
        var id when id.StartsWith("winora.shell.", StringComparison.Ordinal) =>
            ActionJournalCategory.WindowsPersonalization,
        var id when id.StartsWith("winora.startup.", StringComparison.Ordinal) =>
            ActionJournalCategory.Startup,
        var id when id.StartsWith("winora.sounds.", StringComparison.Ordinal) =>
            ActionJournalCategory.SystemSounds,
        var id when id.StartsWith("winora.cursors.", StringComparison.Ordinal) =>
            ActionJournalCategory.Cursors,
        var id when id.StartsWith("winora.cleanup.", StringComparison.Ordinal) =>
            ActionJournalCategory.Retention,
        _ => ActionJournalCategory.Application,
    };

    private static ActionJournalStatus StatusFor(CoordinatorDisposition disposition) => disposition switch
    {
        CoordinatorDisposition.Completed => ActionJournalStatus.Succeeded,
        CoordinatorDisposition.RolledBack or CoordinatorDisposition.AlreadyRestored =>
            ActionJournalStatus.RolledBack,
        CoordinatorDisposition.PartialRecoveryRequired or
            CoordinatorDisposition.Conflict or
            CoordinatorDisposition.DurabilityFailure => ActionJournalStatus.RecoveryRequired,
        _ => ActionJournalStatus.Failed,
    };

    private static ActionJournalRisk RiskFor(RiskLevel risk) => risk switch
    {
        RiskLevel.High => ActionJournalRisk.High,
        RiskLevel.Medium => ActionJournalRisk.Medium,
        _ => ActionJournalRisk.Low,
    };

    private static ActionJournalSupportStatus SupportFor(SupportStatus support) => support switch
    {
        SupportStatus.Supported or SupportStatus.SupportedWithElevation =>
            ActionJournalSupportStatus.Supported,
        SupportStatus.Guided => ActionJournalSupportStatus.Guided,
        _ => ActionJournalSupportStatus.Unsupported,
    };
}
