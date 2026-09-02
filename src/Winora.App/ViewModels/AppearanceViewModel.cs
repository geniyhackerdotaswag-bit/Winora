using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.UI;
using Winora.App.Services;
using Winora.Core.Appearance;

namespace Winora.App.ViewModels;

/// <summary>One ready-made scheme, as a chip in the picker.</summary>
public sealed partial class AppearancePresetViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Id { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Variant { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Color CanvasColor { get; set; }

    [ObservableProperty]
    public partial Color AccentColor { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// Winora's own colours, chosen by the user.
/// </summary>
/// <remarks>
/// <para>
/// Two colours are edited here and the rest is derived. The derived list and the measurement table
/// used to be on screen; the owner had both removed on 2026-09-03, and the code that filled them
/// went with them rather than staying as a collection nothing reads.
/// </para>
/// <para>
/// The measurement itself did not go anywhere. It is the same arithmetic
/// <c>PaletteContrastTests</c> runs over the shipped palette, it still runs on every edit, and it
/// still refuses to let through a scheme whose text falls under the readable floor — that refusal
/// is the only thing the user now sees of it, and only when it fires.
/// </para>
/// <para>
/// Nothing here touches Windows, so nothing here goes through <c>ChangeCoordinator</c>. There is no
/// previous system value to back up and nothing for a rollback to restore.
/// </para>
/// </remarks>
public sealed partial class AppearanceViewModel : ObservableObject
{
    private readonly IThemeBrushService _theme;
    private readonly IColorSchemeStore _store;
    private readonly ILocalizationService _text;
    private readonly IWindowsThemeService _windows;

    private WinoraColorScheme _draft = ColorSchemePresets.Default;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PresetsHeading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CustomHeading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CanvasLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CanvasHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccentLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OnAccentLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OnAccentHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApplyLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResetLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GateTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GateMessage { get; set; } = string.Empty;

    /// <summary>True when the scheme is safe to apply. Drives the button directly.</summary>
    [ObservableProperty]
    public partial bool CanApply { get; set; }

    /// <summary>True when text passes but the accent is nearly invisible — a caution, not a block.</summary>
    [ObservableProperty]
    public partial bool IsWarning { get; set; }

    /// <summary>
    /// Whether the verdict is worth putting on screen at all.
    /// </summary>
    /// <remarks>
    /// A bar that is always there is a bar nobody reads. Silence is the success state: the verdict
    /// appears only when the scheme is blocked or the accent is nearly invisible, and the
    /// measurement table above it is always available for anyone who wants the numbers regardless.
    /// </remarks>
    [ObservableProperty]
    public partial bool ShowGate { get; set; }

    [ObservableProperty]
    public partial bool IsSuppressed { get; set; }

    [ObservableProperty]
    public partial string SuppressedNotice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SavedNotice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WindowsHeading { get; set; } = string.Empty;

    /// <summary>
    /// Which Windows theme the current scheme corresponds to, and why Winora does not set it.
    /// </summary>
    [ObservableProperty]
    public partial string WindowsDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WindowsOpenLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WindowsApplyLabel { get; set; } = string.Empty;

    /// <summary>
    /// What pressing the button will cost, said before it is pressed rather than after.
    /// </summary>
    /// <remarks>
    /// Two things happen that nobody asked for and both are named here: the Windows Settings window
    /// opens, because that is the only way Windows adopts a theme, and Windows stops choosing the
    /// accent from the wallpaper, because a chosen colour and an automatic one cannot both apply.
    /// </remarks>
    [ObservableProperty]
    public partial string WindowsCost { get; set; } = string.Empty;

    /// <summary>True while the change is being applied and confirmed.</summary>
    [ObservableProperty]
    public partial bool IsApplyingToWindows { get; set; }

    /// <summary>False when the live system will not accept the change, with the reason beside it.</summary>
    [ObservableProperty]
    public partial bool CanApplyToWindows { get; set; }

    [ObservableProperty]
    public partial string WindowsResult { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowWindowsResult { get; set; }

    [ObservableProperty]
    public partial bool ShowSaved { get; set; }

    [ObservableProperty]
    public partial Color CanvasColor { get; set; }

    [ObservableProperty]
    public partial Color AccentColor { get; set; }

    [ObservableProperty]
    public partial Color OnAccentColor { get; set; }

    [ObservableProperty]
    public partial string CanvasHex { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccentHex { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OnAccentHex { get; set; } = string.Empty;

    public ObservableCollection<AppearancePresetViewModel> Presets { get; } = [];

    public AppearanceViewModel(
        IThemeBrushService theme,
        IColorSchemeStore store,
        ILocalizationService text,
        IWindowsThemeService windows)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Title = _text.Get("Nav_Appearance");
        PresetsHeading = _text.Get("Appearance_PresetsHeading");
        CustomHeading = _text.Get("Appearance_CustomHeading");
        CanvasLabel = _text.Get("Appearance_Canvas");
        CanvasHint = _text.Get("Appearance_CanvasHint");
        AccentLabel = _text.Get("Appearance_Accent");
        OnAccentLabel = _text.Get("Appearance_OnAccent");
        ApplyLabel = _text.Get("Appearance_Apply");
        ResetLabel = _text.Get("Appearance_Reset");
        WindowsHeading = _text.Get("Appearance_WindowsHeading");
        WindowsOpenLabel = _text.Get("Appearance_WindowsOpen");
        WindowsApplyLabel = _text.Get("Appearance_WindowsApply");
        WindowsCost = _text.Get("Appearance_WindowsCost");
        SavedNotice = _text.Get("Appearance_Applied");
        SuppressedNotice = _text.Get("Appearance_HighContrast");
        IsSuppressed = _theme.IsSuppressed;

        BuildPresets();
        _draft = _theme.CurrentScheme;
        Refresh();

        return RefreshWindowsAsync(cancellationToken);
    }

    /// <summary>
    /// Asks the live system whether it would accept the change, before the button is offered.
    /// </summary>
    private async Task RefreshWindowsAsync(CancellationToken cancellationToken)
    {
        var readiness = await _windows.CanApplyAsync(cancellationToken).ConfigureAwait(true);

        CanApplyToWindows = readiness.CanApply;

        // A grey button with nothing beside it is the shape of a program that knows something and
        // will not say it. Every reason this can give names something to do about it.
        if (!readiness.CanApply)
        {
            WindowsResult = readiness.Reason;
            ShowWindowsResult = readiness.Reason.Length > 0;
        }
    }

    /// <summary>
    /// Carries the saved scheme across to Windows: mode from its lightness, accent from its accent.
    /// </summary>
    /// <remarks>
    /// Only on a press. Changing the appearance of the whole system as a side effect of picking
    /// colours for one application is exactly the high-handedness this program is built against.
    /// </remarks>
    public async Task ApplyToWindowsAsync(CancellationToken cancellationToken = default)
    {
        if (IsApplyingToWindows)
        {
            return;
        }

        IsApplyingToWindows = true;
        ShowWindowsResult = false;

        try
        {
            // The scheme on screen, not the saved one: these are the colours the person is looking
            // at, and a button that sent a different pair would be lying about what it does.
            var palette = SchemeDerivation.Derive(_draft);
            var accent = ((uint)AccentColor.R << 16) | ((uint)AccentColor.G << 8) | AccentColor.B;

            var outcome = await _windows.ApplyAsync(palette.IsDark, accent, cancellationToken)
                .ConfigureAwait(true);

            WindowsResult = outcome.Report;
            ShowWindowsResult = true;
        }
        finally
        {
            IsApplyingToWindows = false;
        }

        await RefreshWindowsAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Picks a ready-made scheme, discarding any manual overrides along with it.</summary>
    public void SelectPreset(string id)
    {
        if (!ColorSchemePresets.TryGet(id, out var preset) || preset is null)
        {
            return;
        }

        // Deliberately the preset's whole scheme, not a merge. A manual colour left over from an
        // earlier experiment would otherwise ride along into every preset tried afterwards, and the
        // presets would each look subtly wrong with nothing on screen to say why.
        _draft = preset.Scheme;
        ShowSaved = false;
        Refresh();
    }

    /// <remarks>
    /// <para>
    /// Each setter ignores a colour that is already in force. That is not an optimisation — it is
    /// what stops a feedback loop. The colour pickers bind their <c>Color</c> to these properties,
    /// so every value this view model pushes comes straight back as a <c>ColorChanged</c> event.
    /// Without the guard, selecting a ready-made scheme immediately converted itself into a custom
    /// one and no preset ever showed as selected, and the automatically chosen colour on the accent
    /// latched itself as a manual override the first time the accent moved.
    /// </para>
    /// </remarks>
    public void SetCanvas(Color colour)
    {
        var value = FromColor(colour);
        if (value == _draft.Canvas)
        {
            return;
        }

        _draft = _draft.AsCustom() with { Canvas = value };
        ShowSaved = false;
        Refresh();
    }

    /// <inheritdoc cref="SetCanvas" />
    public void SetAccent(Color colour)
    {
        var value = FromColor(colour);
        if (value == _draft.Accent)
        {
            return;
        }

        // The colour printed on the accent is cleared with it, so it goes back to being chosen by
        // measurement. Keeping a manual one across an accent change is how a button ends up with a
        // label the same shade as its fill.
        _draft = _draft.AsCustom() with { Accent = value, OnAccent = null };
        ShowSaved = false;
        Refresh();
    }

    /// <inheritdoc cref="SetCanvas" />
    public void SetOnAccent(Color colour)
    {
        var value = FromColor(colour);

        // Compared against the colour in force rather than against the override, so echoing back the
        // automatically chosen one leaves it automatic instead of freezing it.
        if (value == SchemeDerivation.Derive(_draft).OnAccent)
        {
            return;
        }

        _draft = _draft.AsCustom() with { OnAccent = value };
        ShowSaved = false;
        Refresh();
    }

    public void Reset()
    {
        _draft = ColorSchemePresets.Default;
        ShowSaved = false;
        Refresh();
    }

    /// <summary>Paints the draft and writes it to the store.</summary>
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (!CanApply)
        {
            return;
        }

        _theme.Apply(_draft);
        await _store.SaveAsync(_draft, cancellationToken).ConfigureAwait(true);

        IsSuppressed = _theme.IsSuppressed;
        ShowSaved = true;
    }

    private void BuildPresets()
    {
        Presets.Clear();

        foreach (var preset in ColorSchemePresets.All)
        {
            Presets.Add(new AppearancePresetViewModel
            {
                Id = preset.Id,
                Name = _text.Get(preset.NameResourceKey),
                Variant = _text.Get(preset.VariantResourceKey),
                CanvasColor = ToColor(preset.Scheme.Canvas),
                AccentColor = ToColor(preset.Scheme.Accent),
            });
        }
    }

    private void Refresh()
    {
        var palette = SchemeDerivation.Derive(_draft);
        var report = SchemeContrast.Measure(palette);

        CanvasColor = ToColor(_draft.Canvas);
        AccentColor = ToColor(_draft.Accent);
        OnAccentColor = ToColor(palette.OnAccent);
        CanvasHex = _draft.Canvas.ToHex();
        AccentHex = _draft.Accent.ToHex();
        OnAccentHex = palette.OnAccent.ToHex();

        OnAccentHint = _text.Get(_draft.OnAccent is null
            ? "Appearance_OnAccentAuto"
            : "Appearance_OnAccentManual");

        foreach (var preset in Presets)
        {
            preset.IsSelected = string.Equals(preset.Id, _draft.PresetId, StringComparison.Ordinal);
        }

        // Which Windows theme this scheme corresponds to. Winora states it and opens the Windows
        // page; it never writes the value. See the resource string for why.
        WindowsDescription = string.Format(
            CultureInfo.CurrentCulture,
            _text.Get("Appearance_WindowsDescription"),
            _text.Get(palette.IsDark ? "Appearance_Variant_Dark" : "Appearance_Variant_Light"));

        CanApply = report.CanApply;
        IsWarning = report.CanApply && !report.NonTextPasses;

        // Shown only when it refuses, never when it merely advises.
        //
        // The advisory said the accent was quiet and the scheme could be applied anyway. That is a
        // true sentence nobody asked for: the owner picked the colour on purpose and had to read
        // past the same paragraph every time. Removed at their request on 2026-09-03.
        //
        // The refusal stays, and it has to. When text contrast fails, CanApply is false and the
        // apply button greys out — with nothing on screen, that is a button that appears to do
        // nothing, which is the one thing this shell is not allowed to have.
        ShowGate = !report.TextPasses;

        if (!report.TextPasses)
        {
            GateTitle = _text.Get("Appearance_Gate_Blocked");
            GateMessage = _text.Get("Appearance_Gate_BlockedDetail");
        }
        else
        {
            GateTitle = string.Empty;
            GateMessage = string.Empty;
        }
    }

    private static Color ToColor(ColorValue colour) =>
        Color.FromArgb(0xFF, colour.R, colour.G, colour.B);

    private static ColorValue FromColor(Color colour) => new(colour.R, colour.G, colour.B);
}
