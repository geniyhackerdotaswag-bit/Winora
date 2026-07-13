namespace Winora.Core.Contracts;

public interface IMutationLease
{
    ValueTask<IMutationLeaseHandle?> TryAcquireAsync(
        Guid operationId,
        CancellationToken cancellationToken);
}

public interface IMutationLeaseHandle : IAsyncDisposable
{
    long Epoch { get; }
}
