using global::System.ComponentModel;
using global::System.Runtime.InteropServices;
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

        // The owner's markup on the reference screenshot crossed out everything but the card
        // itself: no dark margin, no system border, no caption buttons. RemoveSystemChrome takes
        // the frame and title bar away; RemoveSystemOutline takes the line DWM draws around the
        // window regardless; MakeBackdropTransparent stops the now-square window from painting a
        // solid colour behind the card's rounded corners.
        RemoveSystemChrome();
        RemoveSystemOutline();
        MakeBackdropTransparent();

        // 520 is the card's own Width. The height was 720, sized for the password step; without
        // it the tallest screen is the email one, and the window no longer needs the room. Still
        // fixed rather than resized per step — see the remark on Card in RegistrationWindow.xaml.
        Resize(520, 600);

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

    /// <summary>
    /// The first focus of the run, once there is a tree that can hold it.
    /// </summary>
    /// <remarks>
    /// Tried from the constructor first, and from <c>Activated</c> after that. Neither holds:
    /// nothing can take focus before the tree is built, and on the first activation the framework
    /// assigns focus itself — to the close button, the first thing in the tab order — after the
    /// activation handlers have run, so a focus set there is set and then taken away. Loaded fires
    /// after layout and wins.
    /// </remarks>
    private void OnRootLoaded(object sender, RoutedEventArgs args) => FocusCurrentStep();

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
    /// thing it drives. The array is indexed by the step's own value, so its order has to match
    /// <see cref="RegistrationStep"/> — that is what makes the cast below legal.
    /// </remarks>
    private void ShowStep(RegistrationStep step, bool animate)
    {
        var panels = new UIElement[] { StepName, StepEmail, StepKey, StepDone };

        for (var index = 0; index < panels.Length; index++)
        {
            panels[index].Visibility = index == (int)step
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        Steps.Show(step);

        if (animate)
        {
            Slide(panels[(int)step]);
        }

        FocusCurrentStep();
    }

    /// <summary>
    /// Puts the caret in the field the current step is asking about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the focus goes to the close button, which is simply the first thing in the tab
    /// order, and Enter on a freshly opened window closes the program instead of moving to the next
    /// step. This is the only window shown on a first run, so that is not a small slip: it is the
    /// first thing a person does with Winora.
    /// </para>
    /// <para>
    /// Queued rather than called outright, and at low priority — see the remark at the call.
    /// </para>
    /// <para>
    /// The last step has no field. Focus is left where it is rather than forced onto the button
    /// that opens the app — Enter there would be the same trap in a friendlier costume, one keypress
    /// away from skipping a screen the person has not read.
    /// </para>
    /// </remarks>
    private void FocusCurrentStep()
    {
        Control? field = Model.Step switch
        {
            RegistrationStep.Name => NameField,
            RegistrationStep.Email => EmailField,
            RegistrationStep.Key => KeyField,
            _ => null,
        };

        if (field is null)
        {
            return;
        }

        // Low, not the default: the panel became visible a line ago, and at normal priority this
        // runs before the layout pass that gives it a size. Focusing a control with no size does
        // nothing and reports success.
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => field.Focus(FocusState.Programmatic));
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

    /// <summary>
    /// Closes the only window there is. The specification is explicit that this ends the program,
    /// and with the system caption buttons gone this is now the one control that can do it.
    /// </summary>
    private void OnCloseClicked(object sender, RoutedEventArgs args) => Close();

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
    /// Takes away the system border and title bar, caption buttons included.
    /// </summary>
    /// <remarks>
    /// The card draws its own header and now carries its own close button, so nothing the system
    /// would draw here is wanted. The method this window used to colour the minimise and close
    /// glyphs is gone with them; there is nothing left of that kind to colour.
    /// </remarks>
    private void RemoveSystemChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);

            // The card does not reflow for a different size, so a window the user could stretch or
            // maximise is a window that could show it stretched or maximised.
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
    }

    /// <summary>
    /// Убирает светлую линию, которую Windows обводит вокруг окна.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RemoveSystemChrome"/> снимает рамку окна, но не эту линию: с Windows 11 её
    /// рисует уже не окно, а сам композитор, поверх всего и по своему скруглению. На тёмной
    /// карточке она читается как белая обводка — её и было видно на снимке владельца.
    /// </para>
    /// <para>
    /// Просить прозрачный цвет бесполезно: атрибут принимает COLORREF, у которого нет альфы.
    /// Отказаться от линии целиком можно только особым значением — <c>DWMWA_COLOR_NONE</c>, — и
    /// оно же единственное, что здесь подходит: любой конкретный цвет, даже совпадающий с
    /// карточкой сегодня, разойдётся с ней, как только карточку перекрасят.
    /// </para>
    /// <para>
    /// Отказ молча пропускается, как и в <see cref="MakeBackdropTransparent"/>: до Windows 11
    /// атрибута просто нет, и окно с лишней линией всё равно работает. Это конструктор
    /// единственного окна, которое существует раньше профиля, и исключение отсюда уронило бы
    /// программу до того, как она покажется.
    /// </para>
    /// </remarks>
    private void RemoveSystemOutline()
    {
        try
        {
            var handle = global::WinRT.Interop.WindowNative.GetWindowHandle(this);
            var none = ColourNone;
            _ = DwmSetWindowAttribute(handle, BorderColourAttribute, ref none, sizeof(uint));
        }
        catch (Exception)
        {
            // Nothing to report and nothing a person could do about it.
        }
    }

    /// <summary>
    /// Lets the compositor treat this window's own background as see-through rather than opaque.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setting the root Grid's Background to Transparent in the markup is not enough on its own: an
    /// unpackaged Win32 window's swap chain is opaque by default regardless of what XAML paints
    /// into it, so a merely-transparent-looking Grid would still show as solid black at the four
    /// corners the card's CornerRadius does not cover. Extending the DWM frame across the whole
    /// client area — the "sheet of glass" from the Win32 desktop composition API — is what actually
    /// makes the alpha in those pixels count, and it does so at the HWND level, independent of
    /// WinUI. Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmextendframeintoclientarea
    /// </para>
    /// <para>
    /// Failure is silent and harmless, exactly as in <c>MainWindow.ApplyWindowIcon</c>: a window
    /// that stays opaque still works, and this runs in the constructor of the one window that
    /// exists before a profile does, where a throw would take the whole app down before it ever
    /// appeared.
    /// </para>
    /// </remarks>
    private void MakeBackdropTransparent()
    {
        try
        {
            var handle = global::WinRT.Interop.WindowNative.GetWindowHandle(this);
            var glass = new Margins(-1, -1, -1, -1);
            _ = DwmExtendFrameIntoClientArea(handle, ref glass);
        }
        catch (Exception)
        {
            // Nothing to report and nothing a person could do about it.
        }
    }

    private static SolidColorBrush Solid(byte red, byte green, byte blue) =>
        new(ColorHelper.FromArgb(0xFF, red, green, blue));

    /// <summary>The four margins DWM wants for <see cref="DwmExtendFrameIntoClientArea"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins(int left, int right, int top, int bottom)
    {
        public int Left = left;
        public int Right = right;
        public int Top = top;
        public int Bottom = bottom;
    }

    /// <summary>DWMWA_BORDER_COLOR, the colour of the line DWM draws around a window.</summary>
    private const int BorderColourAttribute = 34;

    /// <summary>DWMWA_COLOR_NONE, the one value that means "draw no line at all".</summary>
    private const uint ColourNone = 0xFFFFFFFE;

    // DllImport rather than LibraryImport: see StartupFailureNotice for why this project reaches
    // for it over the source generator for one narrow interop call.
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);
}
