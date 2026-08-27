using Winora.Core.Contracts;
using Winora.Core.Changes;
using Winora.System.Operations;
using Winora.System.Windows;

namespace Winora.App.Services;

/// <summary>
/// Carries Winora's scheme across to Windows, without letting a ViewModel reach into
/// <c>Winora.System</c> for the vocabulary or the operation.
/// </summary>
/// <remarks>
/// The whole change goes through the same pipeline as every other one Winora makes: a plan, a
/// verified backup of the appearance being replaced, a conditional apply that refuses drift, an
/// independent read to confirm, and a journal entry that can be undone.
/// </remarks>
/// <param name="CanApply">Whether the live system would accept the change.</param>
/// <param name="Reason">Localized text saying why not, or empty when it would.</param>
public sealed record WindowsThemeReadiness(bool CanApply, string Reason);

public interface IWindowsThemeService
{
    /// <summary>
    /// Whether Windows will accept the change, and why not when it will not.
    /// </summary>
    /// <remarks>
    /// The reason comes back with the answer because a button that is simply grey explains nothing,
    /// and every cause here has something the person can do about it.
    /// </remarks>
    Task<WindowsThemeReadiness> CanApplyAsync(CancellationToken cancellationToken);

    /// <summary>Applies a mode and accent, and reports what actually happened.</summary>
    Task<ChangeOutcome> ApplyAsync(bool isDark, uint accentRgb, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class WindowsThemeService : IWindowsThemeService
{
    private readonly IOperationCatalog _catalog;
    private readonly IChangeExecutor _executor;
    private readonly ILocalizationService _text;
    private readonly IThemeLauncher _launcher;

    public WindowsThemeService(
        IOperationCatalog catalog,
        IChangeExecutor executor,
        ILocalizationService text,
        IThemeLauncher launcher)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    public async Task<WindowsThemeReadiness> CanApplyAsync(CancellationToken cancellationToken)
    {
        if (!_catalog.TryResolve(WindowsThemeOperation.Id, out var operation) || operation is null)
        {
            return new WindowsThemeReadiness(false, _text.Get("Result_Blocked"));
        }

        var capability = await operation
            .ProbeAsync(new OperationTarget(WindowsThemeOperation.Id), cancellationToken)
            .ConfigureAwait(true);

        if (capability.Support is SupportStatus.Supported)
        {
            return new WindowsThemeReadiness(true, string.Empty);
        }

        return new WindowsThemeReadiness(
            false,
            _text.Get(capability.BlockReason ?? "Result_Blocked"));
    }

    public async Task<ChangeOutcome> ApplyAsync(
        bool isDark,
        uint accentRgb,
        CancellationToken cancellationToken)
    {
        if (!_catalog.TryResolve(WindowsThemeOperation.Id, out var operation) || operation is null)
        {
            return new ChangeOutcome(false, _text.Get("Result_Blocked"), CoordinatorDisposition.Blocked);
        }

        // Asked here, before the plan, because the pipeline's own refusal cannot say which of many
        // things went wrong — and this one is fixed by closing a window. The operation checks it
        // again at the moment of the change, so a window opened in between still refuses; this only
        // decides whether the person is told something useful or something general.
        if (_launcher.IsSettingsOpen())
        {
            return new ChangeOutcome(
                false,
                _text.Get("Appearance_WindowsSettingsOpen"),
                CoordinatorDisposition.Blocked);
        }

        var wanted = new WindowsThemeSettings(
            isDark ? WindowsThemeMode.Dark : WindowsThemeMode.Light,
            (int)(accentRgb & 0xFFFFFF));

        var draft = new OperationDraft(
            WindowsThemeOperation.Id,
            _text.Get("Appearance_WindowsCategory"),
            _text.Get("Appearance_WindowsPlanTitle"),
            _text.Get("Appearance_WindowsPlanSummary"),
            new OperationTarget(WindowsThemeOperation.Id),
            new DisplayValue(WindowsThemeValues.Kind, WindowsThemeValues.For(wanted)));

        try
        {
            return await _executor.ApplyAsync(operation, draft, cancellationToken).ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            // The operation refuses to plan a change that changes nothing. That is not a failure
            // worth an error: Windows already looks the way the scheme does.
            return new ChangeOutcome(
                true,
                _text.Get("Appearance_WindowsAlready"),
                CoordinatorDisposition.Completed);
        }
    }
}
