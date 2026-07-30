using Winora.Core.Contracts;

namespace Winora.System.Operations;

/// <summary>
/// Resolves operations from a fixed set registered up front, then from factories for domains whose
/// targets are discovered at runtime. Holds no per-session state, so a fresh process reconstructs
/// the same operation from the same id — which is what startup reconciliation of an interrupted
/// change depends on.
/// </summary>
public sealed class CompositeOperationCatalog : IOperationCatalog
{
    private readonly IReadOnlyDictionary<string, IOperation> _known;
    private readonly IReadOnlyList<IOperationFactory> _factories;

    public CompositeOperationCatalog(
        IEnumerable<IOperation> knownOperations,
        IEnumerable<IOperationFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(knownOperations);
        ArgumentNullException.ThrowIfNull(factories);

        _known = knownOperations.ToDictionary(
            static operation => operation.OperationId,
            StringComparer.Ordinal);
        _factories = factories.ToArray();
    }

    public bool TryResolve(string operationId, out IOperation? operation)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            operation = null;
            return false;
        }

        if (_known.TryGetValue(operationId, out var known))
        {
            operation = known;
            return true;
        }

        foreach (var factory in _factories)
        {
            if (factory.TryCreate(operationId, out var created) && created is not null)
            {
                // A factory that returns an operation under a different id would let a plan be
                // applied by something other than the operation it names.
                if (!StringComparer.Ordinal.Equals(created.OperationId, operationId))
                {
                    throw new InvalidOperationException(
                        $"A factory produced '{created.OperationId}' when asked for '{operationId}'.");
                }

                operation = created;
                return true;
            }
        }

        operation = null;
        return false;
    }

    public IOperation Resolve(string operationId) =>
        TryResolve(operationId, out var operation) && operation is not null
            ? operation
            : throw new KeyNotFoundException($"No operation is registered or constructible for '{operationId}'.");
}
