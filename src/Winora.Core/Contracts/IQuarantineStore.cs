namespace Winora.Core.Contracts;

/// <param name="MovedCount">Entries that were relocated.</param>
/// <param name="MovedBytes">Total size of the relocated entries.</param>
/// <param name="SkippedCount">Entries left in place, typically because they were in use.</param>
public sealed record QuarantineMoveResult(int MovedCount, long MovedBytes, int SkippedCount);

/// <summary>
/// Holds reclaimed items somewhere reversible.
/// </summary>
/// <remarks>
/// Declared here rather than beside its implementation so that the layer owning documented Windows
/// operations can depend on the abstraction instead of the layer owning storage, which the
/// dependency rules forbid it from referencing.
/// </remarks>
/// <remarks>
/// Nothing here deletes. Freeing the space is a separate retention decision taken later, which is
/// what keeps the promise that no operation removes user bytes in the step just confirmed.
/// </remarks>
public interface IQuarantineStore
{
    /// <summary>True when this operation currently holds nothing.</summary>
    bool IsEmpty(string operationId);

    /// <summary>
    /// True when a move from this source would be a same-volume rename. A cross-volume source is
    /// refused rather than copied, so the move stays instant and space-neutral.
    /// </summary>
    bool CanAccept(string sourceDirectory);

    QuarantineMoveResult MoveIn(string operationId, string sourceDirectory, CancellationToken cancellationToken);

    QuarantineMoveResult MoveOut(string operationId, string sourceDirectory, CancellationToken cancellationToken);
}
