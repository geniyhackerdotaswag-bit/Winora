namespace Winora.Core.Contracts;

public interface IMutationLease
{
    ValueTask<IMutationLeaseHandle?> TryAcquireAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    ValueTask<IMutationLeaseHandle?> TryAcquireRecoveryAsync(
        Guid incompleteOperationId,
        CancellationToken cancellationToken);
}

public interface IMutationLeaseHandle : IAsyncDisposable
{
    Guid LeaseId { get; }

    Guid OperationId { get; }

    long Epoch { get; }

    bool IsRecoveryTakeover { get; }

    ValueTask<bool> HeartbeatAsync(CancellationToken cancellationToken);

    ValueTask<bool> RevalidateAsync(CancellationToken cancellationToken);
}
