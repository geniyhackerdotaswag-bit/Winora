using Microsoft.Win32;
using Winora.Core.Changes;

namespace Winora.System.Windows;

/// <param name="ValueName">The registry value under Explorer\Advanced.</param>
/// <param name="Slug">Stable lowercase identifier; both the operation and step ids derive from it.</param>
/// <param name="AllowedValues">Every value Microsoft documents for this setting. Nothing else is written.</param>
/// <param name="DocumentedKind">The kind the documentation describes; a live mismatch blocks mutation.</param>
/// <param name="DefaultValue">
///     What Windows applies when the value is absent. Taken from the behaviour of a clean Windows 11
///     installation, not from the reference page, which documents the value set but not the default.
///     Used only to show the user which choice is currently in effect; Winora never writes it on its
///     own, and choosing it explicitly writes the value like any other.
/// </param>
/// <param name="Restart">What the user must do for the change to become visible.</param>
/// <param name="Documentation">The Microsoft Learn page describing this setting.</param>
public sealed record DocumentedShellValue(
    string ValueName,
    string Slug,
    IReadOnlyList<int> AllowedValues,
    int DefaultValue,
    RegistryValueKind DocumentedKind,
    RestartRequirement Restart,
    Uri Documentation)
{
    public string OperationId => $"winora.shell.{Slug}";

    public string StepId => $"shell-{Slug}";
}

/// <summary>
/// The complete set of Explorer values Winora will touch. Anything not listed here is unreachable,
/// which is the point: the catalog is the safety boundary for this domain, not a convenience.
/// </summary>
/// <remarks>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/apps/develop/settings/settings-windows-11
/// <para>
/// Deliberately excluded, with reasons: <c>TaskbarSi</c> has no Learn documentation and Microsoft
/// disabled it; <c>StuckRects3</c> is described by its own documentation as an opaque binary blob;
/// <c>Taskband\FavoritesMigration</c> is explicitly labelled undocumented. <c>UserPreferencesMask</c>
/// and <c>VisualFXSetting</c> are packed legacy blobs with no documented per-bit contract.
/// </para>
/// </remarks>
public static class DocumentedShellValues
{
    private static readonly Uri Reference =
        new("https://learn.microsoft.com/en-us/windows/apps/develop/settings/settings-windows-11");

    public static IReadOnlyList<DocumentedShellValue> All { get; } =
    [
        new("TaskbarAl", "taskbar-alignment", [0, 1], 1, RegistryValueKind.DWord, RestartRequirement.Explorer, Reference),
        new("ShowTaskViewButton", "task-view-button", [0, 1], 1, RegistryValueKind.DWord, RestartRequirement.Explorer, Reference),
        new("TaskbarDa", "taskbar-widgets", [0, 1], 1, RegistryValueKind.DWord, RestartRequirement.Explorer, Reference),
        new("TaskbarGlomLevel", "taskbar-button-grouping", [0, 1, 2], 0, RegistryValueKind.DWord, RestartRequirement.Explorer, Reference),
        new("MMTaskbarGlomLevel", "taskbar-button-grouping-other-displays", [0, 1, 2], 0, RegistryValueKind.DWord, RestartRequirement.Explorer, Reference),
        new("MMTaskbarEnabled", "taskbar-on-all-displays", [0, 1], 1, RegistryValueKind.DWord, RestartRequirement.Explorer, Reference),
        new("MMTaskbarMode", "taskbar-buttons-on-other-displays", [0, 1, 2], 0, RegistryValueKind.DWord, RestartRequirement.Explorer, Reference),
        new("Start_Layout", "start-layout", [0, 1], 0, RegistryValueKind.DWord, RestartRequirement.Explorer, Reference),
        new("Start_TrackDocs", "start-recent-files", [0, 1], 1, RegistryValueKind.DWord, RestartRequirement.Explorer, Reference),
        new("Start_IrisRecommendations", "start-recommendations", [0, 1], 1, RegistryValueKind.DWord, RestartRequirement.Explorer, Reference),
    ];

    public static bool TryFind(string valueName, out DocumentedShellValue entry)
    {
        entry = All.FirstOrDefault(candidate =>
            StringComparer.OrdinalIgnoreCase.Equals(candidate.ValueName, valueName))!;
        return entry is not null;
    }

    public static DocumentedShellValue Find(string valueName) =>
        TryFind(valueName, out var entry)
            ? entry
            : throw new KeyNotFoundException($"'{valueName}' is not a documented Winora shell value.");

    public static bool TryFindByOperationId(string operationId, out DocumentedShellValue entry)
    {
        entry = All.FirstOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.OperationId, operationId))!;
        return entry is not null;
    }
}
