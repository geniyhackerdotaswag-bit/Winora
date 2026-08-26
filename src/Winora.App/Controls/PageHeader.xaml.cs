using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Winora.App.Controls;

/// <summary>The name of the screen.</summary>
/// <remarks>
/// <para>
/// A dependency property rather than a <c>Show</c> method like <see cref="ProfileCard"/> has: the
/// heading is already a property on every page's view model and is already bound in markup, so
/// keeping it bindable means nineteen pages change one line each instead of gaining a code-behind
/// call apiece.
/// </para>
/// <para>
/// It carried a second line for a few hours on 2026-08-26 — one sentence per screen saying what the
/// screen was for. The owner had it taken out of all sixteen: somebody who has just chosen a
/// section from the pane does not need telling what the section is, and the sentence stayed on
/// screen for the whole time they spent there.
/// </para>
/// </remarks>
public sealed partial class PageHeader : UserControl
{
    public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(
        nameof(Heading),
        typeof(string),
        typeof(PageHeader),
        new PropertyMetadata(string.Empty, OnHeadingChanged));

    public PageHeader() => InitializeComponent();

    /// <summary>
    /// The screen's name.
    /// </summary>
    /// <remarks>
    /// Not called Title: <c>UserControl</c> inherits no such property today, but WinUI has added
    /// one to more than one control over the years, and a name that collides later fails as a
    /// silently ignored binding rather than as a build error.
    /// </remarks>
    public string Heading
    {
        get => (string)GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    private static void OnHeadingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is PageHeader header)
        {
            header.HeadingText.Text = (string)args.NewValue ?? string.Empty;
        }
    }
}
