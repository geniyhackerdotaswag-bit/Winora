using Winora.Core.Bypass;
using Winora.Infrastructure.Bypass;

namespace Winora.App.Services;

/// <summary>What this machine has already learned about the strategies.</summary>
public interface IBypassHistory
{
    /// <summary>Every attempt kept, newest first.</summary>
    IReadOnlyList<BypassAttempt> Attempts { get; }

    /// <summary>Records that a strategy was started, with no verdict yet.</summary>
    void Started(string strategyId);

    /// <summary>Settles the verdict on the newest attempt at a strategy.</summary>
    void Settle(string strategyId, BypassOutcome outcome);
}

/// <inheritdoc />
/// <remarks>
/// The adapter that keeps <c>BypassViewModel</c> away from the storage layer, which
/// <c>SolutionStructureTests</c> forbids it from naming. Same shape and same reason as
/// <c>ProfileService</c> over the profile store.
/// </remarks>
public sealed class BypassHistoryService : IBypassHistory
{
    private readonly IBypassAttemptStore _store;
    private readonly TimeProvider _clock;

    private IReadOnlyList<BypassAttempt>? _cached;

    public BypassHistoryService(IBypassAttemptStore store, TimeProvider clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public IReadOnlyList<BypassAttempt> Attempts => _cached ??= _store.Read();

    public void Started(string strategyId)
    {
        if (string.IsNullOrWhiteSpace(strategyId))
        {
            return;
        }

        Save(BypassAttemptRules.Record(
            Attempts,
            new BypassAttempt(strategyId.Trim(), _clock.GetUtcNow(), BypassOutcome.Unknown)));
    }

    public void Settle(string strategyId, BypassOutcome outcome) =>
        Save(BypassAttemptRules.Settle(Attempts, strategyId, outcome));

    /// <remarks>
    /// The list in hand is kept whether or not the file took it. A failed write means the answer is
    /// forgotten by tomorrow, which is a nuisance; dropping it now would forget it mid-search,
    /// while the person is still working through the list, which is the thing this exists to stop.
    /// </remarks>
    private void Save(IReadOnlyList<BypassAttempt> attempts)
    {
        _cached = attempts;
        _ = _store.Write(attempts);
    }
}
