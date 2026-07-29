using System.Security.Cryptography;
using System.Text;

namespace Winora.Core.Changes;

public sealed record ChangePlan
{
    private ChangePlan(
        Guid planId,
        string operationId,
        string category,
        string title,
        string summary,
        IReadOnlyList<ChangeStep> steps,
        RiskLevel risk,
        PrivilegeRequirement privilege,
        RollbackCapability rollback,
        RestartRequirement restart,
        SupportStatus support,
        StateFingerprint sourceFingerprint,
        Uri documentation,
        BackupRequirement backup,
        bool requiresRestorePoint,
        ChangePlanKind kind)
    {
        PlanId = planId;
        OperationId = operationId;
        Category = category;
        Title = title;
        Summary = summary;
        Steps = Array.AsReadOnly(steps.ToArray());
        Risk = risk;
        Privilege = privilege;
        Rollback = rollback;
        Restart = restart;
        Support = support;
        SourceFingerprint = sourceFingerprint;
        Documentation = documentation;
        Backup = backup;
        RequiresRestorePoint = requiresRestorePoint;
        Kind = kind;
        Digest = ComputeDigest(this);
    }

    public Guid PlanId { get; }

    public string OperationId { get; }

    public string Category { get; }

    public string Title { get; }

    public string Summary { get; }

    public IReadOnlyList<ChangeStep> Steps { get; }

    public RiskLevel Risk { get; }

    public PrivilegeRequirement Privilege { get; }

    public RollbackCapability Rollback { get; }

    public RestartRequirement Restart { get; }

    public SupportStatus Support { get; }

    public StateFingerprint SourceFingerprint { get; }

    public Uri Documentation { get; }

    public BackupRequirement Backup { get; }

    public bool RequiresRestorePoint { get; }

    public ChangePlanKind Kind { get; }

    public string Digest { get; }

    public bool HasValidDigest => StringComparer.Ordinal.Equals(Digest, ComputeDigest(this));

    public static ChangePlan Create(
        Guid planId,
        string operationId,
        string category,
        string title,
        string summary,
        IReadOnlyList<ChangeStep> steps,
        RiskLevel risk,
        PrivilegeRequirement privilege,
        RollbackCapability rollback,
        RestartRequirement restart,
        SupportStatus support,
        StateFingerprint sourceFingerprint,
        Uri documentation,
        BackupRequirement backup,
        bool requiresRestorePoint,
        ChangePlanKind kind = ChangePlanKind.TargetMutation)
    {
        if (planId == Guid.Empty)
        {
            throw new ArgumentException("A change plan identifier is required.", nameof(planId));
        }

        if (!IsSafeCatalogOperationId(operationId))
        {
            throw new ArgumentException(
                "An allowlisted catalog operation identifier is required.",
                nameof(operationId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(sourceFingerprint);
        ArgumentNullException.ThrowIfNull(documentation);

        if (steps.Count == 0)
        {
            throw new ArgumentException("A change plan must contain at least one ordered step.", nameof(steps));
        }

        if (steps.Any(step => step is null || !IsSafeStepId(step.StepId)))
        {
            throw new ArgumentException(
                "Every change step must have a display-safe stable identifier.",
                nameof(steps));
        }

        if (steps.Select(step => step.StepId).Distinct(StringComparer.Ordinal).Count() != steps.Count)
        {
            throw new ArgumentException("Change step identifiers must be unique.", nameof(steps));
        }

        if (!documentation.IsAbsoluteUri)
        {
            throw new ArgumentException("Documentation must be an absolute URI.", nameof(documentation));
        }

        return new ChangePlan(
            planId,
            operationId,
            category,
            title,
            summary,
            steps,
            risk,
            privilege,
            rollback,
            restart,
            support,
            sourceFingerprint,
            documentation,
            backup,
            requiresRestorePoint,
            kind);
    }

    internal static string ComputeDigest(ChangePlan plan)
    {
        var canonical = new StringBuilder();
        Append(canonical, plan.PlanId.ToString("D"));
        Append(canonical, plan.OperationId);
        Append(canonical, plan.Category);
        Append(canonical, plan.Title);
        Append(canonical, plan.Summary);
        Append(canonical, plan.Steps.Count);

        foreach (var step in plan.Steps)
        {
            Append(canonical, step.StepId);
            Append(canonical, step.Target.TargetId);
            Append(canonical, step.CurrentValue.Kind);
            Append(canonical, step.CurrentValue.Text);
            Append(canonical, step.ProposedValue.Kind);
            Append(canonical, step.ProposedValue.Text);
            Append(canonical, step.SourceFingerprint.Algorithm);
            Append(canonical, step.SourceFingerprint.Value);
            Append(canonical, step.ResultFingerprint.Algorithm);
            Append(canonical, step.ResultFingerprint.Value);
            Append(canonical, step.Verification.ProbeId);
            Append(canonical, step.Verification.ExpectedResult);
        }

        Append(canonical, (int)plan.Risk);
        Append(canonical, (int)plan.Privilege);
        Append(canonical, (int)plan.Rollback);
        Append(canonical, (int)plan.Restart);
        Append(canonical, (int)plan.Support);
        Append(canonical, plan.SourceFingerprint.Algorithm);
        Append(canonical, plan.SourceFingerprint.Value);
        Append(canonical, plan.Documentation.AbsoluteUri);
        Append(canonical, (int)plan.Backup);
        Append(canonical, plan.RequiresRestorePoint ? 1 : 0);
        Append(canonical, (int)plan.Kind);

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

    internal static bool IsSafeStepId(string? stepId)
    {
        if (string.IsNullOrEmpty(stepId) || stepId.Length > 64)
        {
            return false;
        }

        for (var index = 0; index < stepId.Length; index++)
        {
            var character = stepId[index];
            var isLowerAscii = character is >= 'a' and <= 'z';
            var isDigit = character is >= '0' and <= '9';
            if (isLowerAscii || isDigit || (index > 0 && character is '-' or '_'))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    internal static bool IsSafeCatalogOperationId(string? operationId)
    {
        if (string.IsNullOrEmpty(operationId) || operationId.Length > 96)
        {
            return false;
        }

        var startsSegment = true;
        for (var index = 0; index < operationId.Length; index++)
        {
            var character = operationId[index];
            var isLowerAscii = character is >= 'a' and <= 'z';
            var isDigit = character is >= '0' and <= '9';
            if (startsSegment)
            {
                if (!isLowerAscii)
                {
                    return false;
                }

                startsSegment = false;
                continue;
            }

            if (character == '.')
            {
                if (operationId[index - 1] == '-')
                {
                    return false;
                }

                startsSegment = true;
                continue;
            }

            if (!isLowerAscii && !isDigit && character != '-')
            {
                return false;
            }
        }

        return !startsSegment && operationId[^1] != '-';
    }

    internal static bool IsSafeOpaqueStorageId(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier) || identifier.Length > 96)
        {
            return false;
        }

        for (var index = 0; index < identifier.Length; index++)
        {
            var character = identifier[index];
            var isLowerAscii = character is >= 'a' and <= 'z';
            var isDigit = character is >= '0' and <= '9';
            if (isLowerAscii || isDigit || (index > 0 && character is '-' or '_'))
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
