using Winora.Core.Contracts;

namespace Winora.Infrastructure.Journal;

internal sealed class ActionJournalRetentionService
{
    private readonly IMutationLease _mutationLease;
    private readonly ActionJournalRetentionCoordinator _coordinator;
    private readonly Func<Guid> _transactionIdProvider;

    internal ActionJournalRetentionService(
        IMutationLease mutationLease,
        ActionJournalRetentionCoordinator coordinator,
        Func<Guid>? transactionIdProvider = null)
    {
        _mutationLease = mutationLease ?? throw new ArgumentNullException(nameof(mutationLease));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _transactionIdProvider = transactionIdProvider ?? Guid.NewGuid;
    }

    internal async ValueTask<RetentionMaintenanceResult> RunAsync(
        ActionJournalRetentionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var transactionId = _transactionIdProvider();
        if (transactionId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "The retention transaction identifier provider returned an empty identifier.");
        }

        var lease = await _mutationLease.TryAcquireAsync(
            transactionId,
            cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            throw new RetentionOperationBusyException();
        }

        await using (lease.ConfigureAwait(false))
        {
            ValidateLeaseBinding(lease, transactionId);
            return await _coordinator.RunAsync(
                lease,
                request,
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask<RetentionMaintenanceResult> ResumeAsync(
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A retention transaction identifier is required.",
                nameof(transactionId));
        }

        var lease = await _mutationLease.TryAcquireRecoveryAsync(
            transactionId,
            cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            throw new RetentionOperationBusyException();
        }

        await using (lease.ConfigureAwait(false))
        {
            ValidateLeaseBinding(lease, transactionId);
            return await _coordinator.ResumeAsync(
                lease,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateLeaseBinding(
        IMutationLeaseHandle lease,
        Guid transactionId)
    {
        if (lease.OperationId != transactionId ||
            lease.LeaseId == Guid.Empty ||
            lease.Epoch <= 0)
        {
            throw new InvalidOperationException(
                "The global mutation lease is not bound to the exact retention transaction.");
        }
    }
}

internal sealed class RetentionOperationBusyException : InvalidOperationException
{
    internal RetentionOperationBusyException()
        : base("Another apply, rollback, recovery, or retention operation holds the global mutation lease.")
    {
    }
}
