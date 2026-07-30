using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;
using Winora.Core.Changes;
using Winora.Core.Contracts;

namespace Winora.App.ViewModels;

/// <summary>
/// The first screen. Answers the only question that matters before anything is changed: what can
/// Winora actually do on this machine right now, and why not the rest. Every number here comes from
/// a live probe, never from a count of what was registered.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IReadOnlyList<IOperation> _operations;
    private readonly ISystemSummaryService _system;
    private readonly IDeploymentState _deployment;
    private readonly ILocalizationService _text;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SafetyStatement { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WindowsVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CompatibilityNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCompatible { get; set; }

    [ObservableProperty]
    public partial string DeploymentNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanApplyChanges { get; set; }

    [ObservableProperty]
    public partial string CapabilitySummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BlockedSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasBlocked { get; set; }

    public DashboardViewModel(
        IEnumerable<IOperation> operations,
        ISystemSummaryService system,
        IDeploymentState deployment,
        ILocalizationService text)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _operations = operations.ToArray();
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Dashboard");
        SafetyStatement = _text.Get("App_Safety_Statement");

        var summary = _system.Read();
        WindowsVersion = string.Format(
            CultureInfo.CurrentCulture,
            _text.Get("Dashboard_WindowsVersionFormat"),
            summary.VersionText);
        IsCompatible = summary.MeetsBaseline;
        CompatibilityNote = summary.MeetsBaseline
            ? _text.Get("Dashboard_CompatibleNote")
            : string.Format(
                CultureInfo.CurrentCulture,
                _text.Get("Dashboard_IncompatibleNote"),
                summary.BaselineText);

        CanApplyChanges = _deployment.CanApplyChanges;
        DeploymentNote = _deployment.ApplyBlockReasonKey is { } key
            ? _text.Get(key)
            : _text.Get("Dashboard_CanApplyNote");

        var supported = 0;
        var blocked = 0;
        foreach (var operation in _operations)
        {
            OperationCapability capability;
            try
            {
                capability = await operation
                    .ProbeAsync(new OperationTarget(operation.OperationId), cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                blocked++;
                continue;
            }

            if (capability.Support is SupportStatus.Supported or SupportStatus.SupportedWithElevation)
            {
                supported++;
            }
            else
            {
                blocked++;
            }
        }

        CapabilitySummary = string.Format(
            CultureInfo.CurrentCulture,
            _text.Get("Dashboard_CapabilityFormat"),
            supported,
            _operations.Count);

        HasBlocked = blocked > 0;
        BlockedSummary = string.Format(
            CultureInfo.CurrentCulture,
            _text.Get("Dashboard_BlockedFormat"),
            blocked);
    }
}
