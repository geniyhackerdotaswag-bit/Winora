using Winora.System.Windows;

namespace Winora.App.Services;

/// <param name="Name">The registry value name.</param>
/// <param name="Command">The command line, or empty when the value kind is undocumented.</param>
/// <param name="IsMachineWide">True for HKLM, which needs administrator rights to change.</param>
/// <param name="IsDocumentedKind">False when the value is not a string kind.</param>
public sealed record StartupEntryView(
    string Name,
    string Command,
    bool IsMachineWide,
    bool IsDocumentedKind);

/// <summary>
/// Lists startup entries for the presentation layer without letting a ViewModel reference
/// <c>Winora.System</c> directly. Read-only.
/// </summary>
public interface IStartupInventoryService
{
    IReadOnlyList<StartupEntryView> Read();
}

/// <inheritdoc />
public sealed class StartupInventoryService : IStartupInventoryService
{
    private readonly IRunEntryProbe _probe;

    public StartupInventoryService(IRunEntryProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public IReadOnlyList<StartupEntryView> Read() =>
        _probe.Read()
            .Select(static entry => new StartupEntryView(
                entry.Name,
                entry.Command,
                entry.Scope == RunEntryScope.LocalMachine,
                entry.IsDocumentedKind))
            .OrderBy(static entry => entry.IsMachineWide)
            .ThenBy(static entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
}
