using System.Security.Cryptography;
using System.Text;
using Winora.Core.Contracts;

namespace Winora.Core.Changes;

public sealed record RollbackPlan
{
    private RollbackPlan(
        Guid rollbackId,
        ChangePlan changePlan,
        string backupId,
        string backupDigest,
        StateFingerprint appliedFingerprint,
        StateFingerprint backupFingerprint)
    {
        RollbackId = rollbackId;
        ChangePlan = changePlan;
        BackupId = backupId;
        BackupDigest = backupDigest;
        AppliedFingerprint = appliedFingerprint;
        BackupFingerprint = backupFingerprint;
        Steps = Array.AsReadOnly(changePlan.Steps.Reverse().ToArray());
        Digest = ComputeDigest(this);
    }

    public Guid RollbackId { get; }

    public ChangePlan ChangePlan { get; }

    public string BackupId { get; }

    public string BackupDigest { get; }

    public StateFingerprint AppliedFingerprint { get; }

    public StateFingerprint BackupFingerprint { get; }

    public IReadOnlyList<ChangeStep> Steps { get; }

    public string Digest { get; }

    public bool HasValidDigest => StringComparer.Ordinal.Equals(Digest, ComputeDigest(this));

    public static RollbackPlan Create(
        Guid rollbackId,
        ChangePlan changePlan,
        BackupReceipt linkedBackup,
        StateFingerprint appliedFingerprint)
    {
        if (rollbackId == Guid.Empty)
        {
            throw new ArgumentException("A rollback plan identifier is required.", nameof(rollbackId));
        }

        ArgumentNullException.ThrowIfNull(changePlan);
        ArgumentNullException.ThrowIfNull(linkedBackup);
        if (!linkedBackup.IsVerified ||
            !StringComparer.Ordinal.Equals(linkedBackup.PlanDigest, changePlan.Digest) ||
            linkedBackup.CapturedSourceFingerprint != changePlan.SourceFingerprint ||
            linkedBackup.LiveSourceFingerprint != changePlan.SourceFingerprint)
        {
            throw new ArgumentException(
                "A rollback plan requires a verified backup receipt bound to the exact plan source.",
                nameof(linkedBackup));
        }

        if (!ChangePlan.IsSafeOpaqueStorageId(linkedBackup.BackupId))
        {
            throw new ArgumentException(
                "A rollback backup identifier must be opaque and path-independent.",
                nameof(linkedBackup));
        }

        if (string.IsNullOrWhiteSpace(linkedBackup.BackupDigest))
        {
            throw new ArgumentException(
                "A rollback backup digest is required.",
                nameof(linkedBackup));
        }

        ArgumentNullException.ThrowIfNull(appliedFingerprint);
        return new RollbackPlan(
            rollbackId,
            changePlan,
            linkedBackup.BackupId,
            linkedBackup.BackupDigest,
            appliedFingerprint,
            changePlan.SourceFingerprint);
    }

    private static string ComputeDigest(RollbackPlan plan)
    {
        var canonical = new StringBuilder();
        Append(canonical, plan.RollbackId.ToString("D"));
        Append(canonical, plan.ChangePlan.Digest);
        Append(canonical, plan.BackupId);
        Append(canonical, plan.BackupDigest);
        Append(canonical, plan.AppliedFingerprint.Algorithm);
        Append(canonical, plan.AppliedFingerprint.Value);
        Append(canonical, plan.BackupFingerprint.Algorithm);
        Append(canonical, plan.BackupFingerprint.Value);
        Append(canonical, plan.Steps.Count);
        foreach (var step in plan.Steps)
        {
            Append(canonical, step.StepId);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(Encoding.UTF8.GetByteCount(value));
        builder.Append(':');
        builder.Append(value);
    }

    private static void Append(StringBuilder builder, int value) =>
        Append(builder, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed class RollbackConfirmationToken
{
    internal RollbackConfirmationToken(Guid authorityId, Guid tokenId, string rollbackDigest)
    {
        AuthorityId = authorityId;
        TokenId = tokenId;
        RollbackDigest = rollbackDigest;
    }

    internal Guid AuthorityId { get; }

    internal Guid TokenId { get; }

    internal string RollbackDigest { get; }
}
