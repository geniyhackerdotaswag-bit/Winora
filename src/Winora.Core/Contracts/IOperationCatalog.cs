namespace Winora.Core.Contracts;

/// <summary>
/// Reconstructs an operation from its stable catalog identifier alone.
/// </summary>
/// <remarks>
/// Reconstruction, not lookup, is the requirement. A durable journal records only the operation id,
/// so startup reconciliation of an interrupted change must rebuild the operation in a fresh process
/// where no instance from the original session exists. Domains whose targets are discovered at
/// runtime — startup entries, shortcut icons — therefore encode everything the factory needs in the
/// id itself.
/// </remarks>
public interface IOperationFactory
{
    /// <summary>Builds the operation for <paramref name="operationId"/>, or returns false.</summary>
    bool TryCreate(string operationId, out IOperation? operation);
}

/// <summary>
/// Resolves any operation Winora can perform, whether it was registered up front or belongs to a
/// domain whose targets are discovered at runtime.
/// </summary>
public interface IOperationCatalog
{
    bool TryResolve(string operationId, out IOperation? operation);

    /// <summary>Resolves or throws, for callers that cannot continue without the operation.</summary>
    IOperation Resolve(string operationId);
}
