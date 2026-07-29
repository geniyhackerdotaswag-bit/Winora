using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Winora.Core.Changes;
using Winora.Core.Contracts;

namespace Winora.Infrastructure.Backups;

internal sealed record BackupArtifactDocument(
    string LogicalKey,
    string StorageFileName,
    string Type,
    long Length,
    string Sha256);

internal sealed record BackupManifestDocument(
    string BackupId,
    BackupCaptureKind Kind,
    string PlanDigest,
    StateFingerprint CapturedSourceFingerprint,
    StateFingerprint LiveSourceFingerprint,
    IReadOnlyList<BackupArtifactDocument> Artifacts,
    string BackupDigest)
{
    internal static BackupManifestDocument Create(
        string backupId,
        BackupCaptureKind kind,
        string planDigest,
        StateFingerprint capturedSourceFingerprint,
        StateFingerprint liveSourceFingerprint,
        IReadOnlyList<BackupArtifactDocument> artifacts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planDigest);
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException("A backup manifest has an unknown capture kind.");
        }

        try
        {
            DurableFingerprintPolicy.Validate(
                capturedSourceFingerprint,
                nameof(capturedSourceFingerprint));
            DurableFingerprintPolicy.Validate(
                liveSourceFingerprint,
                nameof(liveSourceFingerprint));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "A backup manifest fingerprint is not canonical SHA-256.",
                exception);
        }

        ArgumentNullException.ThrowIfNull(artifacts);
        var ordered = artifacts.OrderBy(
                artifact => artifact.LogicalKey,
                StringComparer.Ordinal)
            .ToArray();
        if ((ordered.Length == 0 && kind != BackupCaptureKind.WinoraState) ||
            ordered.Select(artifact => artifact.LogicalKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != ordered.Length ||
            ordered.Select(artifact => artifact.StorageFileName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != ordered.Length)
        {
            throw new InvalidDataException("Backup artifact identifiers must be non-empty and unique.");
        }

        foreach (var artifact in ordered)
        {
            BackupStorageName.Validate(artifact);
        }

        var unsigned = new BackupManifestDocument(
            backupId,
            kind,
            planDigest,
            capturedSourceFingerprint,
            liveSourceFingerprint,
            Array.AsReadOnly(ordered),
            string.Empty);
        return unsigned with { BackupDigest = ComputeDigest(unsigned) };
    }

    internal void Validate()
    {
        BackupManifestDocument expected;
        try
        {
            expected = Create(
                BackupId,
                Kind,
                PlanDigest,
                CapturedSourceFingerprint,
                LiveSourceFingerprint,
                Artifacts);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "The backup manifest contains invalid required fields.",
                exception);
        }

        if (!StringComparer.Ordinal.Equals(expected.BackupDigest, BackupDigest))
        {
            throw new InvalidDataException("The backup manifest digest is invalid.");
        }
    }

    private static string ComputeDigest(BackupManifestDocument manifest)
    {
        var canonical = new StringBuilder();
        Append(canonical, manifest.BackupId);
        Append(canonical, ((int)manifest.Kind).ToString(CultureInfo.InvariantCulture));
        Append(canonical, manifest.PlanDigest);
        Append(canonical, manifest.CapturedSourceFingerprint.Algorithm);
        Append(canonical, manifest.CapturedSourceFingerprint.Value);
        Append(canonical, manifest.LiveSourceFingerprint.Algorithm);
        Append(canonical, manifest.LiveSourceFingerprint.Value);
        Append(canonical, manifest.Artifacts.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var artifact in manifest.Artifacts)
        {
            Append(canonical, artifact.LogicalKey);
            Append(canonical, artifact.StorageFileName);
            Append(canonical, artifact.Type);
            Append(canonical, artifact.Length.ToString(CultureInfo.InvariantCulture));
            Append(canonical, artifact.Sha256);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(Encoding.UTF8.GetByteCount(value));
        builder.Append(':');
        builder.Append(value);
    }
}

internal static class BackupStorageName
{
    internal static string ForLogicalKey(string logicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(logicalKey)))
            .ToLowerInvariant() + ".bin";
    }

    internal static void Validate(BackupArtifactDocument artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        try
        {
            _ = BackupArtifact.Create(artifact.LogicalKey, artifact.Type, []);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "A backup manifest contains an invalid logical artifact identifier.",
                exception);
        }

        if (!StringComparer.Ordinal.Equals(
                artifact.StorageFileName,
                ForLogicalKey(artifact.LogicalKey)) ||
            artifact.Length < 0 ||
            artifact.Sha256 is null ||
            artifact.Sha256.Length != 64 ||
            artifact.Sha256.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'A' and <= 'F')))
        {
            throw new InvalidDataException(
                "A backup manifest contains invalid artifact storage metadata.");
        }
    }
}

internal sealed record BackupCommittedMarkerDocument(
    string BackupId,
    string ManifestSha256,
    string BackupDigest,
    DateTimeOffset CommittedUtc);

internal sealed record CommittedBackupManifest(
    BackupManifestDocument Manifest,
    DateTimeOffset CommittedUtc);

internal static class BackupArtifactPath
{
    private static readonly HashSet<string> ReservedDeviceNames = new(
        [
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
            "CONIN$",
            "CONOUT$",
        ],
        StringComparer.OrdinalIgnoreCase);

    internal static string Normalize(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.EndsWith("/", StringComparison.Ordinal) ||
            normalized.Split('/').Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment != segment.Trim() ||
                segment.EndsWith('.') ||
                ReservedDeviceNames.Contains(segment.Split('.', 2)[0]) ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException("A backup artifact path is not a safe relative path.");
        }

        return normalized;
    }

    internal static string CombineUnder(string root, string relativePath)
    {
        var normalized = Normalize(relativePath);
        var path = Path.GetFullPath(Path.Combine(
            root,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("A backup artifact escaped its payload root.");
        }

        return path;
    }
}
