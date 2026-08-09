using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Winora.App.Controls;

/// <summary>
/// A filled line over a series of readings, oldest on the left.
/// </summary>
/// <remarks>
/// <para>
/// The shape Task Manager draws, and it says something a number cannot: whether the load is steady,
/// climbing, or was a spike a moment ago. A percentage on its own hides all three.
/// </para>
/// <para>
/// Drawn here rather than in the view model because a polyline is a WinUI type. The view model hands
/// over plain numbers on a nought-to-one-hundred scale, so it stays testable without a UI.
/// </para>
/// </remarks>
public sealed partial class Sparkline : ContentControl
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
        typeof(Sparkline),
        new PropertyMetadata(null, OnVisualInputChanged));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush),
        typeof(Brush),
        typeof(Sparkline),
        new PropertyMetadata(null, OnVisualInputChanged));

    private readonly Polyline _line = new()
    {
        StrokeThickness = 1.6,
        StrokeLineJoin = PenLineJoin.Round,
    };

    private readonly Polygon _area = new() { Opacity = 0.18 };

    public Sparkline()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        var root = new Grid();
        root.Children.Add(_area);
        root.Children.Add(_line);
        Content = root;

        // The line is laid out in pixels, so it has to be redrawn whenever the box changes size.
        SizeChanged += (_, _) => Redraw();
    }

    /// <summary>Readings on a 0..100 scale, oldest first.</summary>
    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    private static void OnVisualInputChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((Sparkline)sender).Redraw();

    private void Redraw()
    {
        _line.Stroke = LineBrush;
        _area.Fill = LineBrush;

        var values = Values;
        var width = ActualWidth;
        var height = ActualHeight;

        // Fewer than two readings is not a line yet. Drawing a dot would suggest a measurement where
        // there is only a first sample.
        if (values is null || values.Count < 2 || width <= 0 || height <= 0)
        {
            _line.Points.Clear();
            _area.Points.Clear();
            return;
        }

        var step = width / (values.Count - 1);
        var line = new PointCollection();

        for (var index = 0; index < values.Count; index++)
        {
            // Inverted: a reading of a hundred belongs at the top, and the origin is top-left.
            var y = height - (Math.Clamp(values[index], 0, 100) / 100d * height);
            line.Add(new Point(index * step, y));
        }

        _line.Points = line;

        // The same line closed along the bottom edge, so the area beneath it can be tinted.
        var area = new PointCollection { new(0, height) };
        foreach (var point in line)
        {
            area.Add(point);
        }

        area.Add(new Point(width, height));
        _area.Points = area;
    }
}
