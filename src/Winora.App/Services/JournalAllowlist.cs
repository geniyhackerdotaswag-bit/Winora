using Winora.System.Windows;

namespace Winora.App.Services;

/// <summary>
/// The set of catalog identifiers the action journal will accept an entry for.
/// </summary>
/// <remarks>
/// <para>
/// A type of its own, and free of any WinUI reference, so a test can assert over the very set the
/// composition root installs. Assembled inline inside <c>ServiceRegistration</c> the only way to
/// check it was to rebuild the same expression in the test, which proves the test agrees with
/// itself and nothing more.
/// </para>
/// <para>
/// Everything the journal admits is listed here on purpose. An identifier the journal rejects is
/// silently dropped by <c>ActionJournalWriter</c>, by design — a failure to journal must never fail
/// a change that has already happened — so a missing entry here becomes an action with no record
/// rather than a visible error.
/// </para>
/// </remarks>
public static class JournalAllowlist
{
    /// <summary>
    /// Temporary-file reclamation, which is deliberately not a <c>ChangeCoordinator</c> operation
    /// and so contributes no <c>IOperation</c> identifier of its own. It deletes the user's bytes,
    /// making the journal the only record it will ever leave.
    /// </summary>
    public static IReadOnlyList<string> ReclamationOperationIds { get; } =
    [
        .. WindowsTempLocationProbe.AllLocationIds
            .Select(ActionJournalWriter.ReclamationOperationId),
    ];

    /// <param name="registeredOperationIds">Identifiers of the registered <c>IOperation</c> set.</param>
    public static IReadOnlyList<string> CatalogOperationIds(IEnumerable<string> registeredOperationIds)
    {
        ArgumentNullException.ThrowIfNull(registeredOperationIds);
        return [.. registeredOperationIds, .. ReclamationOperationIds];
    }
}
