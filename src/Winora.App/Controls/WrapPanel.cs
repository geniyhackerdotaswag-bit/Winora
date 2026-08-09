using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Winora.App.Controls;

/// <summary>
/// Lays children out in a row and wraps to the next line when the row is full.
/// </summary>
/// <remarks>
/// WinUI has no wrapping panel of its own. The alternative for the appearance screen's swatch chips
/// was a horizontal scroller, which hides half the ready-made schemes behind a scrollbar on a
/// 1280 px window — the point of showing them all at once is that they can be compared.
/// </remarks>
public sealed partial class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing),
            typeof(double),
            typeof(WrapPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing),
            typeof(double),
            typeof(WrapPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // An unbounded width would put every child on one line and report a width nothing can
        // honour. Children are measured unbounded on purpose — each keeps its natural size — but the
        // wrapping decision uses the width actually offered.
        var lineLimit = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;

        double lineWidth = 0;
        double lineHeight = 0;
        double totalWidth = 0;
        double totalHeight = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = child.DesiredSize;

            var advance = lineWidth > 0 ? HorizontalSpacing + desired.Width : desired.Width;
            if (lineWidth > 0 && lineWidth + advance > lineLimit)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + VerticalSpacing;
                lineWidth = desired.Width;
                lineHeight = desired.Height;
                continue;
            }

            lineWidth += advance;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        totalWidth = Math.Max(totalWidth, lineWidth);
        totalHeight += lineHeight;

        return new Size(
            double.IsInfinity(availableSize.Width) ? totalWidth : Math.Min(totalWidth, availableSize.Width),
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        double y = 0;
        double lineHeight = 0;

        foreach (var child in Children)
        {
            var desired = child.DesiredSize;
            var advance = x > 0 ? HorizontalSpacing + desired.Width : desired.Width;

            if (x > 0 && x + advance > finalSize.Width)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
                advance = desired.Width;
            }

            child.Arrange(new Rect(x + (advance - desired.Width), y, desired.Width, desired.Height));
            x += advance;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        return finalSize;
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((WrapPanel)sender).InvalidateMeasure();
}
