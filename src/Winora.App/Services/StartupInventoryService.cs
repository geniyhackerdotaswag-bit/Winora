using Winora.System.Operations;
using Winora.System.Windows;

namespace Winora.App.Services;

/// <param name="Name">The registry value name.</param>
/// <param name="Command">The command line, or empty when the value kind is undocumented.</param>
/// <param name="IsMachineWide">True for HKLM, which needs administrator rights to change.</param>
/// <param name="IsDocumentedKind">False when the value is not a string kind.</param>
/// <param name="OperationId">The operation that can change this entry, or null when none can.</param>
/// <param name="IsEnabled">True when Windows currently launches it at sign-in.</param>
public sealed record StartupEntryView(
    string Name,
    string Command,
    bool IsMachineWide,
    bool IsDocumentedKind,
    string? OperationId,
    bool IsEnabled);

/// <summary>
/// Lists startup entries for the presentation layer without letting a ViewModel reference
/// <c>Winora.System</c> directly.
/// </summary>
public interface IStartupInventoryService
{
    IReadOnlyList<StartupEntryView> Read();
}

/// <inheritdoc />
public sealed class StartupInventoryService : IStartupInventoryService
{
    private readonly IRunEntryProbe _probe;
    private readonly IRunEntryStore _store;

    public StartupInventoryService(IRunEntryProbe probe, IRunEntryStore store)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public IReadOnlyList<StartupEntryView> Read()
    {
        var results = new List<StartupEntryView>();

        // Per-user entries, including the ones Winora is currently holding disabled.
        foreach (var name in _store.EnabledNames().Concat(_store.DisabledNames()).Distinct(StringComparer.Ordinal))
        {
            var reading = _store.Read(name);
            if (reading.State == RunEntryState.Absent)
            {
                continue;
            }

            results.Add(new StartupEntryView(
                name,
                reading.Command ?? string.Empty,
                IsMachineWide: false,
                IsDocumentedKind: true,
                OperationId: RunEntryOperation.IdFor(name),
                IsEnabled: reading.State == RunEntryState.Enabled));
        }

        // Machine-wide entries are shown but not changed. The reason used to be that the app ran
        // without administrator rights and would not ask for them; since the manifest started
        // requiring elevation that is no longer true, and the rights are held. What is missing is
        // the work: WindowsRunEntryStore moves values inside HKEY_CURRENT_USER only, and a
        // machine-wide entry needs its own holding key and its own thinking about what happens to
        // the other people who use this computer.
        foreach (var entry in _probe.Read().Where(static e => e.Scope == RunEntryScope.LocalMachine))
        {
            results.Add(new StartupEntryView(
                entry.Name,
                entry.Command,
                IsMachineWide: true,
                entry.IsDocumentedKind,
                OperationId: null,
                IsEnabled: true));
        }

        return results
            .OrderBy(static entry => entry.IsMachineWide)
            .ThenBy(static entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
