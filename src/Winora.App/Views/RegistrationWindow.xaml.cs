using global::System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Winora.App.Services;
using Winora.App.ViewModels;
using Winora.Core.Profile;

namespace Winora.App.Views;

/// <summary>
/// The only window a new person sees, until they have registered.
/// </summary>
/// <remarks>
/// A window rather than a dialog over the shell: the owner asked that the app itself not be visible
/// until registration is done, and a dialog needs a window under it to sit on.
/// </remarks>
public sealed partial class RegistrationWindow : Window
{
    /// <summary>The card's fixed dark palette, which is not the user's chosen scheme.</summary>
    /// <remarks>
    /// This is the screen shown before anybody has picked a scheme, so it has one of its own, taken
    /// from the reference. Written as bytes rather than parsed from "#RRGGBB" strings: there is no
    /// file these come from and so nothing to be lenient about, and a wrong byte will not compile.
    /// </remarks>
    private static readonly SolidColorBrush Quiet = Solid(0x26, 0x26, 0x2C);
    private static readonly SolidColorBrush Refused = Solid(0xF8, 0x71, 0x71);
    private static readonly SolidColorBrush Accepted = Solid(0x34, 0xD3, 0x99);
    private static readonly SolidColorBrush Weak = Solid(0xF8, 0x71, 0x71);
    private static readonly SolidColorBrush Fair = Solid(0xFB, 0xBF, 0x24);
    private static readonly SolidColorBrush Good = Solid(0x34, 0xD3, 0x99);
    private static readonly SolidColorBrush Unsaid = Solid(0x6E, 0x6E, 0x78);

    /// <summary>The check and the cross on the password checklist, in Segoe Fluent Icons.</summary>
    private const string TickGlyph = "";
    private const string CrossGlyph = "";

    private readonly ILocalizationService _text;

    /// <summary>The slide now running, held so that the next one can stop it.</summary>
    /// <remarks>
    /// A step can be re-entered before the previous 220 ms has finished — Назад, Продолжить, Назад,
    /// pressed faster than the animation plays — and two storyboards on one Opacity do not queue,
    /// they argue: both keep writing, and the panel is left wherever the loser wrote last.
    /// </remarks>
    private Storyboard? _slide;

    public RegistrationWindow()
    {
        Model = App.Services.GetRequiredService<RegistrationViewModel>();
        _text = App.Services.GetRequiredService<ILocalizationService>();

        InitializeComponent();

        Title = _text.Get("Reg_WindowTitle");
        CardTitle.Text = Title;

        // Winora draws its own caption here as it does in the shell. There is no menu and no
        // navigation on this screen, so the only thing the strip has to do is be draggable.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragStrip);
        PaintCaptionButtons();

        Resize(720, 820);

        Model.PropertyChanged += OnModelChanged;
        Model.Completed += OnModelCompleted;

        Closed += (_, _) =>
        {
            Model.PropertyChanged -= OnModelChanged;
            Model.Completed -= OnModelCompleted;
        };

        // No slide for the first screen: there is nothing it could have slid in from.
        ShowStep(Model.Step, animate: false);
    }

    /// <summary>Raised when the profile exists and the app may open.</summary>
    public event EventHandler? Completed;

    /// <summary>The wizard itself. Public because <c>x:Bind</c> resolves against this window.</summary>
    public RegistrationViewModel Model { get; }

    /// <summary>
    /// One user-facing string, by key.
    /// </summary>
    /// <remarks>
    /// Bound as <c>{x:Bind Localized('Reg_Next')}</c>. The alternative — a property on the view model
    /// for every label on four screens — is around thirty properties that each do this and nothing
    /// else. The keys stay visible in the markup beside the control they name, which is where a
    /// person checking the wording would look for them.
    /// </remarks>
    public string Localized(string resourceKey) => _text.Get(resourceKey);

    /// <summary>The border of a field, red once there is something wrong with it.</summary>
    public Brush FieldEdge(string error) => error.Length > 0 ? Refused : Quiet;

    /// <summary>The email field's border: red on a refusal, green once the address will do.</summary>
    public Brush EmailEdge(string error, bool accepted) =>
        error.Length > 0 ? Refused : accepted ? Accepted : Quiet;

    /// <summary>What is wrong with the repeated password, or nothing while it is still being typed.</summary>
    /// <remarks>
    /// Computed here rather than on the view model, which has no such property: it is the one thing
    /// on these screens that exists purely to be looked at. What it says is already enforced by
    /// <see cref="RegistrationViewModel.CanFinish"/>, which is what actually refuses the save.
    /// </remarks>
    public string ConfirmError(string password, string confirm) =>
        confirm.Length > 0 && !string.Equals(password, confirm, StringComparison.Ordinal)
            ? _text.Get("Reg_ConfirmMismatch")
            : string.Empty;

    /// <summary>Whether <see cref="ConfirmError"/> has anything to show.</summary>
    /// <remarks>
    /// Its own function because <c>x:Bind</c> cannot nest one function binding inside another.
    /// </remarks>
    public Visibility ConfirmErrorShown(string password, string confirm) =>
        ConfirmError(password, confirm).Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The repeat field's border, red once the two no longer agree.</summary>
    public Brush ConfirmEdge(string password, string confirm) =>
        FieldEdge(ConfirmError(password, confirm));

    /// <summary>The word for how strong the password is.</summary>
    /// <remarks>
    /// Written as five literal keys rather than as "Reg_Strength_" plus a number, so that the
    /// architecture test which checks every requested key against the resource file can see them.
    /// </remarks>
    public string StrengthLabel(PasswordStrength strength)
    {
        ArgumentNullException.ThrowIfNull(strength);

        return strength.Score switch
        {
            1 => _text.Get("Reg_Strength_1"),
            2 => _text.Get("Reg_Strength_2"),
            3 => _text.Get("Reg_Strength_3"),
            4 => _text.Get("Reg_Strength_4"),
            _ => _text.Get("Reg_Strength_0"),
        };
    }

    /// <summary>The colour of that word: grey, red, amber, green.</summary>
    public Brush StrengthEdge(PasswordStrength strength)
    {
        ArgumentNullException.ThrowIfNull(strength);

        return strength.Score switch
        {
            1 => Weak,
            2 => Fair,
            >= 3 => Good,
            _ => Unsaid,
        };
    }

    /// <summary>A met requirement or an unmet one, on the checklist.</summary>
    public string RuleGlyph(bool met) => met ? TickGlyph : CrossGlyph;

    /// <summary>Green for a met requirement; the checklist's own grey for one still outstanding.</summary>
    /// <remarks>
    /// Grey rather than red for the unmet ones: a person halfway through typing a password has not
    /// done anything wrong, and a column of red crosses says they have.
    /// </remarks>
    public Brush RuleEdge(bool met) => met ? Accepted : Unsaid;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(RegistrationViewModel.Step))
        {
            ShowStep(Model.Step, animate: true);
        }
    }

    private void OnModelCompleted(object? sender, EventArgs args) =>
        Completed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Swaps the visible step, sliding the new one in.
    /// </summary>
    /// <remarks>
    /// Done by hand rather than by a transition on a Frame: there are four fixed panels, and a
    /// navigation stack for four panels that never navigate anywhere is more machinery than the
    /// thing it drives.
    /// </remarks>
    private void ShowStep(RegistrationStep step, bool animate)
    {
        var panels = new UIElement[] { StepName, StepEmail, StepPassword, StepDone };

        for (var index = 0; index < panels.Length; index++)
        {
            panels[index].Visibility = index == (int)step
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (step == RegistrationStep.Done)
        {
            // The view model wipes the plain text the moment it has been hashed. These two controls
            // are the other copy of it, and leaving them holding it would make that wipe a gesture.
            PasswordField.Password = string.Empty;
            ConfirmField.Password = string.Empty;
        }

        Steps.Show(step);

        if (animate)
        {
            Slide(panels[(int)step]);
        }
    }

    private void Slide(UIElement target)
    {
        _slide?.Stop();

        // The values a panel rests at, written as the base values rather than as the start of the
        // animation: both animations below carry an explicit From, so stopping either of them mid
        // flight leaves the panel where it belongs — in place and visible — instead of stranded at
        // the opacity it happened to have been started from.
        var transform = new TranslateTransform();
        target.RenderTransform = transform;
        target.Opacity = 1;

        var storyboard = new Storyboard();

        var move = new DoubleAnimation
        {
            From = 24,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(move, transform);
        Storyboard.SetTargetProperty(move, "X");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
        };

        Storyboard.SetTarget(fade, target);
        Storyboard.SetTargetProperty(fade, "Opacity");

        storyboard.Children.Add(move);
        storyboard.Children.Add(fade);

        _slide = storyboard;
        storyboard.Begin();
    }

    /// <summary>The name has been left, so it may now be told it is too short.</summary>
    private void OnNameLostFocus(object sender, RoutedEventArgs args) => Model.NameTouched = true;

    private void OnEmailLostFocus(object sender, RoutedEventArgs args) => Model.EmailTouched = true;

    /// <summary>
    /// Carries the typed password to the view model.
    /// </summary>
    /// <remarks>
    /// By hand rather than by a two-way binding. <see cref="PasswordBox.Password"/> is the one field
    /// on these screens where the control, not the model, is the thing somebody would think to read
    /// at a breakpoint, and an explicit line here is easier to be sure of than a binding mode.
    /// </remarks>
    private void OnPasswordTyped(object sender, RoutedEventArgs args) =>
        Model.Password = PasswordField.Password;

    private void OnConfirmTyped(object sender, RoutedEventArgs args) =>
        Model.Confirm = ConfirmField.Password;

    /// <summary>
    /// Puts an offered domain onto whatever has been typed, replacing any domain already there.
    /// </summary>
    private void OnDomainChosen(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Content: string domain })
        {
            return;
        }

        var typed = Model.Email.Trim();
        var at = typed.IndexOf('@', StringComparison.Ordinal);
        Model.Email = (at >= 0 ? typed[..at] : typed) + domain;
    }

    /// <summary>Sizes the window and puts it in the middle of the screen it opened on.</summary>
    /// <remarks>
    /// Centred by hand because this is the first thing the program ever shows: a welcome that opens
    /// in a corner reads as something that got away from its author.
    /// </remarks>
    private void Resize(int width, int height)
    {
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);

        if (area is null)
        {
            return;
        }

        AppWindow.Move(new Windows.Graphics.PointInt32(
            area.WorkArea.X + ((area.WorkArea.Width - width) / 2),
            area.WorkArea.Y + ((area.WorkArea.Height - height) / 2)));
    }

    /// <summary>
    /// The minimise and close glyphs.
    /// </summary>
    /// <remarks>
    /// They belong to the window rather than to the visual tree, so no resource in the markup
    /// reaches them. Left alone on a machine set to the light system theme they would be drawn dark
    /// on this card's near-black background and simply disappear.
    /// </remarks>
    private void PaintCaptionButtons()
    {
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = ColorHelper.FromArgb(0xFF, 0xC9, 0xC9, 0xD1);
        titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(0xFF, 0x6E, 0x6E, 0x78);
        titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(0xFF, 0x1B, 0x1B, 0x21);
        titleBar.ButtonHoverForegroundColor = ColorHelper.FromArgb(0xFF, 0xF4, 0xF4, 0xF6);
        titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(0xFF, 0x26, 0x26, 0x2C);
        titleBar.ButtonPressedForegroundColor = ColorHelper.FromArgb(0xFF, 0xF4, 0xF4, 0xF6);
    }

    private static SolidColorBrush Solid(byte red, byte green, byte blue) =>
        new(ColorHelper.FromArgb(0xFF, red, green, blue));
}
