using Winora.Core.Changes;
using Winora.Core.Contracts;

namespace Winora.Infrastructure.Operations;

internal sealed record VerifiedOperationChain(
    IReadOnlyList<OperationTransitionDocument> Events,
    DurableOperationBoundary? Boundary)
{
    internal OperationTransitionDocument? LastEvent => Events.Count == 0 ? null : Events[^1];

    internal static VerifiedOperationChain Rebuild(
        Guid operationId,
        IEnumerable<OperationTransitionDocument> events)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A durable operation identifier is required.", nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(events);
        var ordered = events.OrderBy(item => item.Revision).ToArray();
        if (ordered.Length == 0)
        {
            return new VerifiedOperationChain([], null);
        }

        var verifiedStepIds = new List<string>();
        OperationTransitionDocument? previous = null;
        OperationTransition? previousTransition = null;
        OperationTransition? current = null;
        for (var index = 0; index < ordered.Length; index++)
        {
            var document = ordered[index];
            if (document.OperationId != operationId || document.Revision != index + 1L)
            {
                throw new InvalidDataException(
                    "The operation transition chain is not contiguous for the requested operation.");
            }

            current = document.Rehydrate(previous);
            ValidateProgress(current, previousTransition, verifiedStepIds);
            previous = document;
            previousTransition = current;
        }

        var transition = current!;
        var boundary = DurableOperationBoundary.Create(
            operationId,
            transition.Facts,
            ordered[^1].Revision,
            transition.State,
            transition.StepId,
            Array.AsReadOnly(verifiedStepIds.ToArray()),
            transition.RestorePoint);
        return new VerifiedOperationChain(Array.AsReadOnly(ordered), boundary);
    }

    private static void ValidateProgress(
        OperationTransition transition,
        OperationTransition? previous,
        ICollection<string> verifiedStepIds)
    {
        switch (transition.State)
        {
            case OperationState.Applying:
                RequireNextStep(transition, verifiedStepIds);
                break;
            case OperationState.Applied:
                RequireSameStep(previous, transition, OperationState.Applying);
                RequireNextStep(transition, verifiedStepIds);
                break;
            case OperationState.ApplyStepNotApplied:
                RequireSameStep(previous, transition, OperationState.Applying);
                RequireNextStep(transition, verifiedStepIds);
                break;
            case OperationState.ApplyFailedNoChanges:
                RequireSameStep(previous, transition, OperationState.ApplyStepNotApplied);
                if (verifiedStepIds.Count != 0)
                {
                    throw InvalidProgress(
                        "ApplyFailedNoChanges requires an empty verified step prefix.");
                }

                RequireNextStep(transition, verifiedStepIds);
                break;
            case OperationState.VerificationFailedRollbackOffered:
                RequireSameStep(previous, transition, OperationState.Applied);
                RequireNextStep(transition, verifiedStepIds);
                break;
            case OperationState.Verified:
                if (previous?.State == OperationState.RestorePointEnded)
                {
                    if (transition.Facts.Kind == ChangePlanKind.ManualRestorePointArtifact)
                    {
                        RecordNextVerifiedStep(transition, verifiedStepIds);
                    }
                    else if (transition.StepId is null ||
                             verifiedStepIds.Count != transition.Facts.OrderedStepIds.Count ||
                             !StringComparer.Ordinal.Equals(
                                 transition.StepId,
                                 transition.Facts.OrderedStepIds[^1]))
                    {
                        throw InvalidProgress(
                            "A post-restore verification must acknowledge the already verified final step.");
                    }
                }
                else
                {
                    RequireSameStep(previous, transition, OperationState.Applied);
                    RecordNextVerifiedStep(transition, verifiedStepIds);
                }

                break;
            case OperationState.RollingBack:
                RequireNextStep(transition, verifiedStepIds);
                break;
            case OperationState.RollbackApplied:
                RequireSameStep(previous, transition, OperationState.RollingBack);
                RequireNextStep(transition, verifiedStepIds);
                break;
            case OperationState.RollbackVerified:
                RequireSameStep(previous, transition, OperationState.RollbackApplied);
                RecordNextVerifiedStep(transition, verifiedStepIds);
                break;
            case OperationState.AlreadyRestored when previous?.State == OperationState.RollingBack:
                RequireSameStep(previous, transition, OperationState.RollingBack);
                RecordNextVerifiedStep(transition, verifiedStepIds);
                break;
            case OperationState.AlreadyRestored:
                if (previous?.State != OperationState.RollbackPlanned ||
                    transition.StepId is not null ||
                    verifiedStepIds.Count != 0)
                {
                    throw InvalidProgress(
                        "Aggregate AlreadyRestored is only valid before any rollback step mutation.");
                }

                break;
            case OperationState.RecoveryConflictExternalDrift
                when previous?.StepId is not null:
                RequireSameStep(previous, transition, previous.State);
                break;
            case OperationState.Completed:
                RequireFullCoverage(transition, verifiedStepIds);
                break;
            case OperationState.RolledBack:
                var aggregateAlreadyRestored =
                    previous?.State == OperationState.AlreadyRestored &&
                    previous.StepId is null &&
                    verifiedStepIds.Count == 0;
                if (!aggregateAlreadyRestored)
                {
                    RequireFullCoverage(transition, verifiedStepIds);
                }

                break;
        }
    }

    private static void RequireSameStep(
        OperationTransition? previous,
        OperationTransition transition,
        OperationState requiredPreviousState)
    {
        if (previous?.State != requiredPreviousState ||
            previous.StepId is null ||
            !StringComparer.Ordinal.Equals(previous.StepId, transition.StepId))
        {
            throw InvalidProgress(
                $"{requiredPreviousState} and {transition.State} must reference the same durable step.");
        }
    }

    private static void RequireNextStep(
        OperationTransition transition,
        ICollection<string> verifiedStepIds)
    {
        if (transition.StepId is null ||
            verifiedStepIds.Count >= transition.Facts.OrderedStepIds.Count ||
            !StringComparer.Ordinal.Equals(
                transition.StepId,
                transition.Facts.OrderedStepIds[verifiedStepIds.Count]))
        {
            throw InvalidProgress(
                "A mutation step must be the next member of the exact ordered verified prefix.");
        }
    }

    private static void RecordNextVerifiedStep(
        OperationTransition transition,
        ICollection<string> verifiedStepIds)
    {
        RequireNextStep(transition, verifiedStepIds);
        verifiedStepIds.Add(transition.StepId!);
    }

    private static void RequireFullCoverage(
        OperationTransition transition,
        ICollection<string> verifiedStepIds)
    {
        if (!verifiedStepIds.SequenceEqual(
                transition.Facts.OrderedStepIds,
                StringComparer.Ordinal))
        {
            throw InvalidProgress(
                $"{transition.State} requires verified coverage of every ordered durable step.");
        }
    }

    private static InvalidDataException InvalidProgress(string message) =>
        new($"The durable operation step chain is invalid. {message}");
}
