using Winora.Core.Changes;
using Winora.Core.Contracts;

namespace Winora.Infrastructure.Journal;

public sealed class FixedActionJournalOperationCatalog : IActionJournalOperationCatalog
{
    private readonly HashSet<string> _operationIds;

    public FixedActionJournalOperationCatalog(IEnumerable<string> operationIds)
    {
        ArgumentNullException.ThrowIfNull(operationIds);
        _operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operationId in operationIds)
        {
            if (!ChangePlan.IsSafeCatalogOperationId(operationId))
            {
                throw new ArgumentException(
                    "Every action-journal catalog identifier must use the Core operation-ID grammar.",
                    nameof(operationIds));
            }

            if (!_operationIds.Add(operationId))
            {
                throw new ArgumentException(
                    "The action-journal operation allowlist contains a duplicate identifier.",
                    nameof(operationIds));
            }
        }

        if (_operationIds.Count == 0)
        {
            throw new ArgumentException(
                "The action-journal operation allowlist cannot be empty.",
                nameof(operationIds));
        }
    }

    public bool IsAllowlisted(string catalogOperationId) =>
        catalogOperationId is not null && _operationIds.Contains(catalogOperationId);
}
