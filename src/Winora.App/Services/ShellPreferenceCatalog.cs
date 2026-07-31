using Winora.System.Windows;

namespace Winora.App.Services;

/// <summary>
/// Exposes the documented value set for a shell preference to the presentation layer without
/// letting a ViewModel reference <c>Winora.System</c> directly.
/// </summary>
public interface IShellPreferenceCatalog
{
    IReadOnlyList<int> AllowedValuesFor(string operationId);

    /// <summary>What Windows applies when the value is absent, so the list can show it as selected.</summary>
    int DefaultValueFor(string operationId);
}

/// <inheritdoc />
public sealed class ShellPreferenceCatalog : IShellPreferenceCatalog
{
    public IReadOnlyList<int> AllowedValuesFor(string operationId) => Find(operationId).AllowedValues;

    public int DefaultValueFor(string operationId) => Find(operationId).DefaultValue;

    private static DocumentedShellValue Find(string operationId) =>
        DocumentedShellValues.TryFindByOperationId(operationId, out var entry)
            ? entry
            : throw new KeyNotFoundException($"'{operationId}' is not a documented Winora shell value.");
}
