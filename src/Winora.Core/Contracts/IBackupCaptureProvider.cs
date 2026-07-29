using System.Security.Cryptography;
using Winora.Core.Changes;

namespace Winora.Core.Contracts;

public enum BackupCaptureKind
{
    Operation,
    RecoveryCheckpoint,
    WinoraState,
}

public sealed record BackupArtifact
{
    private readonly byte[] _content;

    private BackupArtifact(string key, string type, byte[] content)
    {
        Key = key;
        Type = type;
        _content = content;
        ContentSha256 = Convert.ToHexString(SHA256.HashData(content));
    }

    public string Key { get; }

    public string Type { get; }

    public ReadOnlyMemory<byte> Content => _content.ToArray();

    public int Length => _content.Length;

    public string ContentSha256 { get; }

    public static BackupArtifact Create(
        string key,
        string type,
        ReadOnlySpan<byte> content)
    {
        ValidateLogicalKey(key, nameof(key), allowHierarchy: true);
        ValidateLogicalKey(type, nameof(type), allowHierarchy: false);
        return new BackupArtifact(key, type, content.ToArray());
    }

    private static void ValidateLogicalKey(
        string value,
        string parameterName,
        bool allowHierarchy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 256 ||
            value != value.Trim() ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            value.EndsWith("/", StringComparison.Ordinal) ||
            (!allowHierarchy && value.Contains('/', StringComparison.Ordinal)) ||
            value.Split('/').Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.Any(character =>
                    character is not (>= 'a' and <= 'z') and
                        not (>= '0' and <= '9') and
                        not '-' and
                        not '_' and
                        not '.')))
        {
            throw new ArgumentException(
                "Backup artifact identifiers must be stable lower-case logical keys.",
                parameterName);
        }
    }
}

public sealed record BackupCapture
{
    private BackupCapture(
        BackupCaptureKind kind,
        StateFingerprint capturedSourceFingerprint,
        StateFingerprint liveSourceFingerprint,
        IReadOnlyList<BackupArtifact> artifacts)
    {
        Kind = kind;
        CapturedSourceFingerprint = capturedSourceFingerprint;
        LiveSourceFingerprint = liveSourceFingerprint;
        Artifacts = Array.AsReadOnly(artifacts.ToArray());
    }

    public BackupCaptureKind Kind { get; }

    public StateFingerprint CapturedSourceFingerprint { get; }

    public StateFingerprint LiveSourceFingerprint { get; }

    public IReadOnlyList<BackupArtifact> Artifacts { get; }

    public static BackupCapture ForOperation(
        StateFingerprint capturedSourceFingerprint,
        StateFingerprint liveSourceFingerprint,
        IReadOnlyList<BackupArtifact> artifacts) =>
        Create(
            BackupCaptureKind.Operation,
            capturedSourceFingerprint,
            liveSourceFingerprint,
            artifacts,
            allowEmpty: false);

    public static BackupCapture ForRecoveryCheckpoint(
        StateFingerprint capturedSourceFingerprint,
        StateFingerprint liveSourceFingerprint,
        IReadOnlyList<BackupArtifact> artifacts) =>
        Create(
            BackupCaptureKind.RecoveryCheckpoint,
            capturedSourceFingerprint,
            liveSourceFingerprint,
            artifacts,
            allowEmpty: false);

    internal static BackupCapture ForWinoraState(
        StateFingerprint capturedSourceFingerprint,
        StateFingerprint liveSourceFingerprint,
        IReadOnlyList<BackupArtifact> artifacts) =>
        Create(
            BackupCaptureKind.WinoraState,
            capturedSourceFingerprint,
            liveSourceFingerprint,
            artifacts,
            allowEmpty: true);

    private static BackupCapture Create(
        BackupCaptureKind kind,
        StateFingerprint capturedSourceFingerprint,
        StateFingerprint liveSourceFingerprint,
        IReadOnlyList<BackupArtifact> artifacts,
        bool allowEmpty)
    {
        ValidateFingerprint(capturedSourceFingerprint, nameof(capturedSourceFingerprint));
        ValidateFingerprint(liveSourceFingerprint, nameof(liveSourceFingerprint));
        ArgumentNullException.ThrowIfNull(artifacts);
        if ((!allowEmpty && artifacts.Count == 0) ||
            artifacts.Any(artifact => artifact is null) ||
            artifacts.Select(artifact => artifact.Key)
                .Distinct(StringComparer.Ordinal)
                .Count() != artifacts.Count)
        {
            throw new ArgumentException(
                "Backup artifacts must have unique stable keys and satisfy the capture kind.",
                nameof(artifacts));
        }

        return new BackupCapture(
            kind,
            capturedSourceFingerprint,
            liveSourceFingerprint,
            artifacts);
    }

    private static void ValidateFingerprint(
        StateFingerprint fingerprint,
        string parameterName) =>
        DurableFingerprintPolicy.Validate(fingerprint, parameterName);
}

public interface IBackupCaptureProvider
{
    ValueTask<BackupCapture> CaptureOperationAsync(
        ChangePlan plan,
        CancellationToken cancellationToken);

    ValueTask<BackupCapture> CaptureRecoveryCheckpointAsync(
        RollbackPlan plan,
        CancellationToken cancellationToken);
}
