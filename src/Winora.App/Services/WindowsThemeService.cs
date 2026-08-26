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
public interface IWindowsThemeService
{
    /// <summary>Whether Windows will accept the change at all, asked of the live system.</summary>
    Task<bool> CanApplyAsync(CancellationToken cancellationToken);

    /// <summary>Applies a mode and accent, and reports what actually happened.</summary>
    Task<ChangeOutcome> ApplyAsync(bool isDark, uint accentRgb, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class WindowsThemeService : IWindowsThemeService
{
    private readonly IOperationCatalog _catalog;
    private readonly IChangeExecutor _executor;
    private readonly ILocalizationService _text;

    public WindowsThemeService(
        IOperationCatalog catalog,
        IChangeExecutor executor,
        ILocalizationService text)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public async Task<bool> CanApplyAsync(CancellationToken cancellationToken)
    {
        if (!_catalog.TryResolve(WindowsThemeOperation.Id, out var operation) || operation is null)
        {
            return false;
        }

        var capability = await operation
            .ProbeAsync(new OperationTarget(WindowsThemeOperation.Id), cancellationToken)
            .ConfigureAwait(true);

        return capability.Support is SupportStatus.Supported;
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
