using Winora.System.Windows;

namespace Winora.App.Services;

/// <summary>
/// Exposes the documented value set for a shell preference to the presentation layer without
/// letting a ViewModel reference <c>Winora.System</c> directly.
/// </summary>
public interface IShellPreferenceCatalog
{
    IReadOnlyList<int> AllowedValuesFor(string operationId);
}

/// <inheritdoc />
public sealed class ShellPreferenceCatalog : IShellPreferenceCatalog
{
    public IReadOnlyList<int> AllowedValuesFor(string operationId) =>
        DocumentedShellValues.TryFindByOperationId(operationId, out var entry)
            ? entry.AllowedValues
            : throw new KeyNotFoundException($"'{operationId}' is not a documented Winora shell value.");
}
