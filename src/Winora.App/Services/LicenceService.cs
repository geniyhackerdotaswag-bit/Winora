using Winora.Core.Licence;
using Winora.Infrastructure.Licence;
using Winora.System.Licence;

namespace Winora.App.Services;

/// <summary>Activating a key, and knowing where the subscription stands.</summary>
public interface ILicenceService
{
    /// <summary>What is known right now, without asking the site.</summary>
    LicenceState Current { get; }

    /// <summary>The stored key's tail, masked for showing. Empty when none is stored.</summary>
    string MaskedKey { get; }

    /// <summary>Whether this build knows where the site is.</summary>
    bool IsConfigured { get; }

    /// <summary>Trades a key for a token and keeps both the token and what came with it.</summary>
    Task<LicenceResult> ActivateAsync(string key, string? promoCode, CancellationToken cancellationToken);

    /// <summary>Asks the site about the stored token, if there is one and it is due.</summary>
    Task<LicenceResult> RefreshAsync(bool force, CancellationToken cancellationToken);

    /// <summary>Forgets the key on this machine.</summary>
    bool Forget();
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Nothing in Winora is gated on the answer yet, and that is deliberate rather than unfinished: the
/// owner has not said which parts are paid, and a gate put in before that decision would be a guess
/// standing between people and a program that works today.
/// </para>
/// <para>
/// What this does is the part that has to exist first — a key can be entered, the site is asked,
/// and the answer is kept and shown.
/// </para>
/// </remarks>
public sealed class LicenceService : ILicenceService
{
    private readonly ILicenceClient _client;
    private readonly ILicenceStore _store;
    private readonly TimeProvider _time;

    private string _key = string.Empty;

    public LicenceService(ILicenceClient client, ILicenceStore store, TimeProvider? time = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? TimeProvider.System;
    }

    public LicenceState Current => _store.Read();

    /// <remarks>
    /// Only the tail, and only for a key entered in this session. The key itself is never written
    /// down — the token is what gets stored — so after a restart there is nothing to mask, and the
    /// screen shows the plan instead. That is the intended trade: a stolen store file yields a
    /// token the owner can revoke, not a key somebody can use on their own machine.
    /// </remarks>
    public string MaskedKey => LicenceKey.Mask(_key);

    public bool IsConfigured => LicenceEndpoint.IsConfigured;

    public async Task<LicenceResult> ActivateAsync(
        string key,
        string? promoCode,
        CancellationToken cancellationToken)
    {
        var (result, token) = await _client
            .ActivateAsync(key, Environment.MachineName, Blank(promoCode), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return result;
        }

        _key = key;

        // Kept only after the site agreed. Storing on the way in would leave a machine claiming a
        // subscription the site refused.
        return _store.Write(token, result.State)
            ? result
            : LicenceResult.Failed(LicenceOutcome.Unreachable);
    }

    public async Task<LicenceResult> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        var stored = _store.Read();
        var token = _store.Token;

        if (!stored.Exists || token.Length == 0)
        {
            return LicenceResult.Failed(LicenceOutcome.Rejected);
        }

        if (!force && !stored.NeedsRecheck(_time.GetUtcNow()))
        {
            return new LicenceResult(LicenceOutcome.Confirmed, stored);
        }

        var result = await _client.CheckAsync(token, cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            // The machine name is not sent back by the check, so the one already stored is kept.
            _store.Write(token, result.State with { Machine = stored.Machine });
            return result;
        }

        /*
         * A site that cannot be reached does not end a subscription.
         *
         * People use Winora when their machine is misbehaving, which is exactly when the network
         * is least likely to work. Treating silence as expiry would take the program away at the
         * worst moment, and would do it on the strength of no evidence at all. The stored state
         * stands until the site actually says otherwise.
         */
        if (result.Outcome is LicenceOutcome.Unreachable or LicenceOutcome.NotConfigured)
        {
            return new LicenceResult(result.Outcome, stored);
        }

        // The site did answer, and the answer was no. Now the stored copy is wrong and goes.
        _store.Clear();
        _key = string.Empty;
        return result;
    }

    public bool Forget()
    {
        _key = string.Empty;
        return _store.Clear();
    }

    private static string? Blank(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
