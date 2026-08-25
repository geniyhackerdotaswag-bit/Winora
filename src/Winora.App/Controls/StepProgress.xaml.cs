using global::System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Winora.App.Services;
using Winora.App.ViewModels;

namespace Winora.App.Controls;

/// <summary>Where the person is in the registration wizard: three circles on a line.</summary>
/// <remarks>
/// It reads <see cref="RegistrationStep"/> and nothing else. It cannot be clicked, unlike the
/// reference the owner supplied — jumping back to a visited step is the Back button's job, and a
/// second way to do the same thing is a second thing that can disagree with the first.
/// </remarks>
public sealed partial class StepProgress : UserControl
{
    /// <summary>How many circles there are; the fourth step (Done) lights all of them.</summary>
    private const int Count = 3;

    private static readonly SolidColorBrush Lit = Solid(0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush OnLit = Solid(0x0A, 0x0A, 0x0C);
    private static readonly SolidColorBrush Well = Solid(0x0E, 0x0E, 0x12);
    private static readonly SolidColorBrush Edge = Solid(0x23, 0x23, 0x29);
    private static readonly SolidColorBrush Near = Solid(0xE6, 0xE6, 0xEA);
    private static readonly SolidColorBrush Far = Solid(0x6E, 0x6E, 0x78);
    private static readonly SolidColorBrush Nothing = new(Colors.Transparent);

    private readonly Ellipse[] _rings;
    private readonly TextBlock[] _numbers;
    private readonly FontIcon[] _ticks;
    private readonly TextBlock[] _labels;

    public StepProgress()
    {
        InitializeComponent();

        _rings = [Ring1, Ring2, Ring3];
        _numbers = [Number1, Number2, Number3];
        _ticks = [Tick1, Tick2, Tick3];
        _labels = [Label1, Label2, Label3];

        var text = App.Services.GetRequiredService<ILocalizationService>();
        Label1.Text = text.Get("Reg_StepName");
        Label2.Text = text.Get("Reg_StepEmail");
        Label3.Text = text.Get("Reg_StepPassword");

        for (var index = 0; index < Count; index++)
        {
            // A digit, not a word, so there is nothing here to translate. Formatted with the
            // current culture all the same: some of them do not write these with Arabic numerals.
            _numbers[index].Text = (index + 1).ToString(CultureInfo.CurrentCulture);
        }

        Show(RegistrationStep.Name);
    }

    /// <summary>Paints the circles for the step now showing.</summary>
    /// <remarks>
    /// Filled by hand rather than by binding, following <see cref="ProfileCard"/>: one enum in, four
    /// brushes and a length out. Bindings for that would need three converters and would still be
    /// this method, only spread across two files.
    /// </remarks>
    public void Show(RegistrationStep step)
    {
        var index = (int)step;

        for (var circle = 0; circle < Count; circle++)
        {
            var isDone = index > circle;
            var isCurrent = index == circle;

            _rings[circle].Fill = isDone ? Lit : Well;
            _rings[circle].Stroke = isCurrent ? Lit : isDone ? Nothing : Edge;
            _rings[circle].StrokeThickness = isCurrent ? 2 : 1;

            _ticks[circle].Foreground = OnLit;
            _ticks[circle].Visibility = isDone ? Visibility.Visible : Visibility.Collapsed;

            _numbers[circle].Foreground = isCurrent ? Lit : Far;
            _numbers[circle].Visibility = isDone ? Visibility.Collapsed : Visibility.Visible;

            _labels[circle].Foreground = isDone || isCurrent ? Near : Far;
        }

        // The reference fills the line to the current step's share of it, which is why the last of
        // the three sits at the far end and fills it completely. The success screen is past the end
        // of the line, so it holds there rather than overshooting.
        var reached = Math.Min(index, Count - 1);
        Fill(reached / (double)(Count - 1));
    }

    /// <summary>Runs the lit part of the line out to <paramref name="fraction"/> of its length.</summary>
    /// <remarks>
    /// Set outright while the control has not been shown yet. The first step is painted from the
    /// constructor, before there is anything on screen, and animating a line nobody can see is at
    /// best wasted and at worst a storyboard begun on an element with no visual tree behind it.
    /// </remarks>
    private void Fill(double fraction)
    {
        if (!IsLoaded)
        {
            TrackScale.ScaleX = fraction;
            return;
        }

        var storyboard = new Storyboard();

        var grow = new DoubleAnimation
        {
            To = fraction,
            Duration = new Duration(TimeSpan.FromMilliseconds(500)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(grow, TrackScale);
        Storyboard.SetTargetProperty(grow, "ScaleX");
        storyboard.Children.Add(grow);
        storyboard.Begin();
    }

    private static SolidColorBrush Solid(byte red, byte green, byte blue) =>
        new(ColorHelper.FromArgb(0xFF, red, green, blue));
}
