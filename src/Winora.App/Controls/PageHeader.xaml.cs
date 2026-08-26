using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winora.App.Services;

namespace Winora.App.Controls;

/// <summary>The name of the screen, and one line saying what it is for.</summary>
/// <remarks>
/// Dependency properties rather than a <c>Show</c> method like <see cref="ProfileCard"/> has: the
/// heading is already a property on every page's view model and is already bound in markup, so
/// keeping it bindable means nineteen pages change one line each instead of gaining a code-behind
/// call apiece.
/// </remarks>
public sealed partial class PageHeader : UserControl
{
    public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(
        nameof(Heading),
        typeof(string),
        typeof(PageHeader),
        new PropertyMetadata(string.Empty, OnHeadingChanged));

    /// <summary>
    /// Resource key for the line beside the name, resolved here.
    /// </summary>
    /// <remarks>
    /// Resolved by the control rather than by each page. The alternative was a
    /// <c>public string Sub(string key)</c> on fourteen code-behinds, each one line long and each
    /// identical — the shape that ends with thirteen of them agreeing and one drifting.
    /// </remarks>
    public static readonly DependencyProperty SubtitleKeyProperty = DependencyProperty.Register(
        nameof(SubtitleKey),
        typeof(string),
        typeof(PageHeader),
        new PropertyMetadata(string.Empty, OnSubtitleKeyChanged));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle),
        typeof(string),
        typeof(PageHeader),
        new PropertyMetadata(string.Empty, OnSubtitleChanged));

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

    /// <summary>Resource key for the subtitle. Use this or <see cref="Subtitle"/>, not both.</summary>
    public string SubtitleKey
    {
        get => (string)GetValue(SubtitleKeyProperty);
        set => SetValue(SubtitleKeyProperty, value);
    }

    /// <summary>What the screen is for, in one line. Empty draws nothing.</summary>
    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    private static void OnHeadingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is PageHeader header)
        {
            header.HeadingText.Text = (string)args.NewValue ?? string.Empty;
        }
    }

    private static void OnSubtitleKeyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not PageHeader header || (string)args.NewValue is not { Length: > 0 } key)
        {
            return;
        }

        header.Subtitle = App.Services.GetRequiredService<ILocalizationService>().Get(key);
    }

    private static void OnSubtitleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not PageHeader header)
        {
            return;
        }

        var value = (string)args.NewValue ?? string.Empty;
        header.SubtitleText.Text = value;
        header.SubtitleText.Visibility = value.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
