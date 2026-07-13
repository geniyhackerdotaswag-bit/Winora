using System.Collections.Concurrent;

namespace Winora.Core.Changes;

public sealed class ConfirmationAuthority
{
    private readonly Guid _authorityId = Guid.NewGuid();
    private readonly ConcurrentDictionary<Guid, string> _applyConfirmations = new();
    private readonly ConcurrentDictionary<Guid, string> _rollbackConfirmations = new();

    public ConfirmationToken Confirm(ChangePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var token = new ConfirmationToken(_authorityId, Guid.NewGuid(), plan.Digest);
        if (!_applyConfirmations.TryAdd(token.TokenId, plan.Digest))
        {
            throw new InvalidOperationException("The confirmation capability could not be registered.");
        }

        return token;
    }

    public RollbackConfirmationToken Confirm(RollbackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var token = new RollbackConfirmationToken(_authorityId, Guid.NewGuid(), plan.Digest);
        if (!_rollbackConfirmations.TryAdd(token.TokenId, plan.Digest))
        {
            throw new InvalidOperationException("The rollback confirmation capability could not be registered.");
        }

        return token;
    }

    public bool Authorizes(ConfirmationToken confirmation, ChangePlan plan) =>
        confirmation is not null &&
        plan is not null &&
        plan.HasValidDigest &&
        confirmation.AuthorityId == _authorityId &&
        _applyConfirmations.TryGetValue(confirmation.TokenId, out var registeredDigest) &&
        StringComparer.Ordinal.Equals(registeredDigest, confirmation.PlanDigest) &&
        StringComparer.Ordinal.Equals(registeredDigest, plan.Digest);

    public bool Authorizes(RollbackConfirmationToken confirmation, RollbackPlan plan) =>
        confirmation is not null &&
        plan is not null &&
        plan.HasValidDigest &&
        confirmation.AuthorityId == _authorityId &&
        _rollbackConfirmations.TryGetValue(confirmation.TokenId, out var registeredDigest) &&
        StringComparer.Ordinal.Equals(registeredDigest, confirmation.RollbackDigest) &&
        StringComparer.Ordinal.Equals(registeredDigest, plan.Digest);

    internal bool TryAuthorize(ConfirmationToken confirmation, ChangePlan plan) =>
        Authorizes(confirmation, plan) &&
        _applyConfirmations.TryRemove(confirmation.TokenId, out _);

    internal bool TryAuthorize(RollbackConfirmationToken confirmation, RollbackPlan plan) =>
        Authorizes(confirmation, plan) &&
        _rollbackConfirmations.TryRemove(confirmation.TokenId, out _);
}
