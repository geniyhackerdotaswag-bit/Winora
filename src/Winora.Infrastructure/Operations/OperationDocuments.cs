using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Winora.Core.Changes;
using Winora.Core.Contracts;

namespace Winora.Infrastructure.Operations;

public enum DurableJournalActor
{
    App,
    ElevatedHost,
}

internal sealed record DurableStepRecoveryDescriptorDocument(
    string StepId,
    string RecoveryKey,
    StateFingerprint SourceFingerprint,
    StateFingerprint ResultFingerprint,
    string DescriptorDigest)
{
    internal static DurableStepRecoveryDescriptorDocument From(
        DurableStepRecoveryDescriptor descriptor) =>
        new(
            descriptor.StepId,
            descriptor.RecoveryKey,
            descriptor.SourceFingerprint,
            descriptor.ResultFingerprint,
            descriptor.Digest);

    internal DurableStepRecoveryDescriptor Rehydrate()
    {
        var descriptor = DurableStepRecoveryDescriptor.Rehydrate(
            StepId,
            RecoveryKey,
            SourceFingerprint,
            ResultFingerprint);
        if (!StringComparer.Ordinal.Equals(descriptor.Digest, DescriptorDigest))
        {
            throw new InvalidDataException(
                "A persisted recovery-step descriptor has an invalid digest.");
        }

        return descriptor;
    }
}

internal sealed record DurableOperationFactsDocument(
    string CatalogOperationId,
    string PlanDigest,
    StateFingerprint SourceFingerprint,
    IReadOnlyList<string> OrderedStepIds,
    IReadOnlyList<DurableStepRecoveryDescriptorDocument> RecoverySteps,
    PrivilegeRequirement Privilege,
    RiskLevel Risk,
    RollbackCapability Rollback,
    BackupRequirement Backup,
    bool RequiresRestorePoint,
    ChangePlanKind Kind,
    string? BackupId,
    string? BackupDigest,
    string? RecoveryCheckpointId,
    string? RecoveryCheckpointDigest,
    StateFingerprint? BackupFingerprint,
    string FactsDigest)
{
    internal static DurableOperationFactsDocument From(DurableOperationFacts facts) =>
        new(
            facts.CatalogOperationId,
            facts.PlanDigest,
            facts.SourceFingerprint,
            Array.AsReadOnly(facts.OrderedStepIds.ToArray()),
            Array.AsReadOnly(
                facts.RecoverySteps.Select(DurableStepRecoveryDescriptorDocument.From).ToArray()),
            facts.Privilege,
            facts.Risk,
            facts.Rollback,
            facts.Backup,
            facts.RequiresRestorePoint,
            facts.Kind,
            facts.BackupId,
            facts.BackupDigest,
            facts.RecoveryCheckpointId,
            facts.RecoveryCheckpointDigest,
            facts.BackupFingerprint,
            facts.Digest);

    internal DurableOperationFacts Rehydrate()
    {
        if (RecoverySteps is null)
        {
            throw new InvalidDataException(
                "Persisted durable operation facts are missing recovery descriptors.");
        }

        var facts = DurableOperationFacts.Rehydrate(
            CatalogOperationId,
            PlanDigest,
            SourceFingerprint,
            OrderedStepIds,
            RecoverySteps.Select(descriptor => descriptor.Rehydrate()).ToArray(),
            Privilege,
            Risk,
            Rollback,
            Backup,
            RequiresRestorePoint,
            Kind,
            BackupId,
            BackupDigest,
            RecoveryCheckpointId,
            RecoveryCheckpointDigest,
            BackupFingerprint);
        if (!StringComparer.Ordinal.Equals(facts.Digest, FactsDigest))
        {
            throw new InvalidDataException("Persisted durable operation facts have an invalid digest.");
        }

        return facts;
    }
}

internal sealed record DurableFingerprintFactDocument(
    FingerprintFactKind Kind,
    StateFingerprint? Value,
    string Digest)
{
    internal static DurableFingerprintFactDocument From(DurableFingerprintFact fact) =>
        new(fact.Kind, fact.Value, fact.Digest);

    internal DurableFingerprintFact Rehydrate()
    {
        var fact = DurableFingerprintFact.Rehydrate(Kind, Value);
        if (!StringComparer.Ordinal.Equals(fact.Digest, Digest))
        {
            throw new InvalidDataException("Persisted fingerprint facts have an invalid digest.");
        }

        return fact;
    }
}

internal sealed record OperationTransitionMetadataDocument(
    DurableFingerprintFactDocument ExpectedFingerprint,
    DurableFingerprintFactDocument ResultFingerprint,
    DurableOperationErrorCode ErrorCode,
    string MetadataDigest)
{
    internal static OperationTransitionMetadataDocument From(OperationTransitionMetadata metadata) =>
        new(
            DurableFingerprintFactDocument.From(metadata.ExpectedFingerprint),
            DurableFingerprintFactDocument.From(metadata.ResultFingerprint),
            metadata.ErrorCode,
            metadata.Digest);

    internal OperationTransitionMetadata Rehydrate()
    {
        var metadata = OperationTransitionMetadata.Create(
            ExpectedFingerprint.Rehydrate(),
            ResultFingerprint.Rehydrate(),
            ErrorCode);
        if (!StringComparer.Ordinal.Equals(metadata.Digest, MetadataDigest))
        {
            throw new InvalidDataException("Persisted transition metadata has an invalid digest.");
        }

        return metadata;
    }
}

internal sealed record RestorePointTransitionFactsDocument(
    Guid CorrelationId,
    string Description,
    StateFingerprint PreBeginInventoryFingerprint,
    StateFingerprint? PostBeginInventoryFingerprint,
    long? SequenceNumber,
    RestorePointApiStatus BeginApiStatus,
    RestorePointOwnershipStatus OwnershipStatus,
    RestorePointFinalizationMode FinalizationMode,
    DateTimeOffset? BeginReturnedAtUtc,
    DateTimeOffset? FinalizationRequestedAtUtc,
    RestorePointApiStatus FinalizationApiStatus,
    string RestorePointFactsDigest)
{
    internal static RestorePointTransitionFactsDocument From(RestorePointTransitionFacts facts) =>
        new(
            facts.CorrelationId,
            facts.Description,
            facts.PreBeginInventoryFingerprint,
            facts.PostBeginInventoryFingerprint,
            facts.SequenceNumber,
            facts.BeginApiStatus,
            facts.OwnershipStatus,
            facts.FinalizationMode,
            facts.BeginReturnedAtUtc,
            facts.FinalizationRequestedAtUtc,
            facts.FinalizationApiStatus,
            facts.Digest);

    internal RestorePointTransitionFacts Rehydrate()
    {
        var facts = RestorePointTransitionFacts.Create(
            CorrelationId,
            Description,
            PreBeginInventoryFingerprint,
            PostBeginInventoryFingerprint,
            SequenceNumber,
            BeginApiStatus,
            OwnershipStatus,
            FinalizationMode,
            BeginReturnedAtUtc,
            FinalizationRequestedAtUtc,
            FinalizationApiStatus);
        if (!StringComparer.Ordinal.Equals(facts.Digest, RestorePointFactsDigest))
        {
            throw new InvalidDataException("Persisted restore-point facts have an invalid digest.");
        }

        return facts;
    }
}

internal sealed record OperationTransitionDocument(
    string TransitionId,
    Guid OperationId,
    long Revision,
    long ExpectedRevision,
    OperationState? ExpectedState,
    OperationState State,
    string? StepId,
    DateTimeOffset OccurredAtUtc,
    DurableJournalActor Actor,
    string? PreviousEventHash,
    DurableOperationFactsDocument Facts,
    OperationTransitionMetadataDocument Metadata,
    RestorePointTransitionFactsDocument? RestorePoint,
    string CoreTransitionHash,
    string EventHash)
{
    internal static OperationTransitionDocument Create(
        OperationTransition transition,
        DurableJournalActor actor,
        string? previousEventHash)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (!Enum.IsDefined(actor))
        {
            throw new ArgumentOutOfRangeException(nameof(actor));
        }

        var unsigned = new OperationTransitionDocument(
            Guid.NewGuid().ToString("N"),
            transition.OperationId,
            transition.ExpectedRevision + 1,
            transition.ExpectedRevision,
            transition.ExpectedState,
            transition.State,
            transition.StepId,
            transition.OccurredAtUtc,
            actor,
            previousEventHash,
            DurableOperationFactsDocument.From(transition.Facts),
            OperationTransitionMetadataDocument.From(transition.Metadata),
            transition.RestorePoint is null
                ? null
                : RestorePointTransitionFactsDocument.From(transition.RestorePoint),
            transition.TransitionHash,
            string.Empty);
        return unsigned with { EventHash = ComputeEventHash(unsigned) };
    }

    internal OperationTransition Rehydrate(
        OperationTransitionDocument? previous)
    {
        if (TransitionId is null ||
            TransitionId.Length != 32 ||
            TransitionId.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "A durable transition has an invalid canonical identifier.");
        }

        if (Revision != ExpectedRevision + 1 || Revision <= 0)
        {
            throw new InvalidDataException("A durable transition has a non-monotonic revision.");
        }

        if (!Enum.IsDefined(Actor))
        {
            throw new InvalidDataException("A durable transition has an unknown actor.");
        }

        if (!Enum.IsDefined(State) ||
            (ExpectedState is { } expectedState && !Enum.IsDefined(expectedState)))
        {
            throw new InvalidDataException("A durable transition has an unknown state.");
        }

        if (OccurredAtUtc == default || OccurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "A durable transition timestamp must be a non-default UTC value.");
        }

        if (Facts is null || Metadata is null)
        {
            throw new InvalidDataException(
                "A durable transition is missing required typed facts.");
        }

        var expectedPreviousHash = previous?.EventHash;
        if (!StringComparer.Ordinal.Equals(PreviousEventHash, expectedPreviousHash) ||
            ExpectedRevision != (previous?.Revision ?? 0) ||
            ExpectedState != previous?.State ||
            OperationId != (previous?.OperationId ?? OperationId))
        {
            throw new InvalidDataException("The immutable transition chain has a broken predecessor link.");
        }

        var expectedEventHash = ComputeEventHash(this with { EventHash = string.Empty });
        if (!StringComparer.Ordinal.Equals(EventHash, expectedEventHash))
        {
            throw new InvalidDataException("A durable transition event hash is invalid.");
        }

        DurableOperationFacts facts;
        OperationTransitionMetadata metadata;
        RestorePointTransitionFacts? restorePoint;
        DurableOperationFacts? previousFacts;
        RestorePointTransitionFacts? previousRestorePoint;
        try
        {
            facts = Facts.Rehydrate();
            metadata = Metadata.Rehydrate();
            restorePoint = RestorePoint?.Rehydrate();
            previousFacts = previous?.Facts.Rehydrate();
            previousRestorePoint = previous?.RestorePoint?.Rehydrate();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "A durable transition contains unknown or invalid typed facts.",
                exception);
        }
        OperationTransition transition;
        try
        {
            transition = OperationTransition.Create(
                OperationId,
                facts,
                ExpectedRevision,
                ExpectedState,
                State,
                StepId,
                OccurredAtUtc,
                restorePoint,
                previousRestorePoint,
                previousFacts,
                metadata);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("A durable transition violates the Core state contract.", exception);
        }

        if (!StringComparer.Ordinal.Equals(transition.TransitionHash, CoreTransitionHash))
        {
            throw new InvalidDataException("A durable transition Core hash is invalid.");
        }

        return transition;
    }

    private static string ComputeEventHash(OperationTransitionDocument document)
    {
        var canonical = new StringBuilder();
        Append(canonical, document.TransitionId);
        Append(canonical, document.OperationId.ToString("D"));
        Append(canonical, document.Revision.ToString(CultureInfo.InvariantCulture));
        Append(canonical, document.ExpectedRevision.ToString(CultureInfo.InvariantCulture));
        AppendOptional(canonical, document.ExpectedState?.ToString());
        Append(canonical, document.State.ToString());
        AppendOptional(canonical, document.StepId);
        Append(canonical, document.OccurredAtUtc.ToString("O"));
        Append(canonical, ((int)document.Actor).ToString(CultureInfo.InvariantCulture));
        AppendOptional(canonical, document.PreviousEventHash);
        Append(canonical, document.Facts.FactsDigest);
        Append(canonical, document.Metadata.MetadataDigest);
        AppendOptional(canonical, document.RestorePoint?.RestorePointFactsDigest);
        Append(canonical, document.CoreTransitionHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendOptional(StringBuilder builder, string? value)
    {
        Append(builder, value is null ? "0" : "1");
        if (value is not null)
        {
            Append(builder, value);
        }
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(Encoding.UTF8.GetByteCount(value));
        builder.Append(':');
        builder.Append(value);
    }
}

internal sealed record OperationProjectionDocument(
    Guid OperationId,
    long Revision,
    OperationState State,
    string? StepId,
    string LastEventHash,
    string FactsDigest,
    IReadOnlyList<string> AppliedStepIds,
    string? RestorePointFactsDigest)
{
    internal static OperationProjectionDocument From(
        DurableOperationBoundary boundary,
        string lastEventHash) =>
        new(
            boundary.OperationId,
            boundary.Revision,
            boundary.State,
            boundary.StepId,
            lastEventHash,
            boundary.Facts.Digest,
            Array.AsReadOnly(boundary.AppliedStepIds.ToArray()),
            boundary.RestorePoint?.Digest);
}
