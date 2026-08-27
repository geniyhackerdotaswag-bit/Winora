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
/// <param name="Domain">
///     Which screen the setting belongs to, and the middle segment of its operation identifier.
/// </param>
/// <remarks>
///     The domain is part of the identifier rather than a label beside it, because each screen finds
///     its own rows by asking for a prefix. It defaults to <c>shell</c> so that every identifier
///     already written to a journal or a saved plan still resolves to the same operation — those are
///     durable records, and an id that quietly changed meaning would leave an old entry pointing at
///     nothing.
/// </remarks>
public sealed record DocumentedShellValue(
    string ValueName,
    string Slug,
    IReadOnlyList<int> AllowedValues,
    int DefaultValue,
    RegistryValueKind DocumentedKind,
    RestartRequirement Restart,
    Uri Documentation,
    string Domain = "shell")
{
    public string OperationId => $"winora.{Domain}.{Slug}";

    public string StepId => $"{Domain}-{Slug}";
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

    /// <summary>
    /// Where Microsoft documents the two File Explorer values, and their registry paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The open specification for Group Policy preferences. It names both values, both full paths
    /// and what each controls, which is more than the settings page above does for them.
    /// </para>
    /// <para>
    /// Its wording is not what decided the numbers. <c>hideFileExt</c> is described there as
    /// "Displays known file extensions. MUST be 1 to enable" — which reads as the opposite of what
    /// a value called <em>Hide</em>FileExt does. The mapping was settled by asking the shell itself
    /// through the documented <c>SHGetSettings</c> on 2026-08-27: with <c>HideFileExt=0</c> it
    /// reported <c>fShowExtensions</c> true, and with <c>Hidden=2</c> it reported
    /// <c>fShowAllObjects</c> false. Documented paths, measured meanings.
    /// </para>
    /// </remarks>
    private static readonly Uri ExplorerReference = new(
        "https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gppref/" +
        "3c837e92-016e-4148-86e5-b4f0381a757f");

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

        // File Explorer. Two values, and deliberately only two.
        //
        // ShowSuperHidden is documented on the same page and is not here: showing protected
        // operating system files is a way to delete something Windows depends on, and Microsoft's
        // own dialog puts a warning in front of it. Offering that switch beside "show file
        // extensions" would put a loaded choice next to a harmless one and call them the same kind
        // of thing.
        //
        // Extensions first, because a hidden extension is how a file named photo.jpg.exe passes for
        // a photograph — and Windows hides them by default.
        new("HideFileExt", "file-extensions", [0, 1], 1, RegistryValueKind.DWord,
            RestartRequirement.Explorer, ExplorerReference, "explorer"),
        new("Hidden", "hidden-files", [1, 2], 2, RegistryValueKind.DWord,
            RestartRequirement.Explorer, ExplorerReference, "explorer"),

        // The rest of what the same specification documents and Winora is willing to offer.
        new("ShowInfoTip", "info-tips", [0, 1], 1, RegistryValueKind.DWord,
            RestartRequirement.Explorer, ExplorerReference, "explorer"),
        new("FolderContentsInfoTip", "folder-size-tips", [0, 1], 1, RegistryValueKind.DWord,
            RestartRequirement.Explorer, ExplorerReference, "explorer"),
        new("ShowCompColor", "compressed-in-colour", [0, 1], 0, RegistryValueKind.DWord,
            RestartRequirement.Explorer, ExplorerReference, "explorer"),
        new("PersistBrowsers", "reopen-folders", [0, 1], 0, RegistryValueKind.DWord,
            RestartRequirement.Explorer, ExplorerReference, "explorer"),
        new("SeparateProcess", "separate-process", [0, 1], 0, RegistryValueKind.DWord,
            RestartRequirement.Explorer, ExplorerReference, "explorer"),
        new("NoNetCrawling", "network-search", [0, 1], 1, RegistryValueKind.DWord,
            RestartRequirement.Explorer, ExplorerReference, "explorer"),
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
