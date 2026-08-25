using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Winora.App.ViewModels;

namespace Winora.App.Controls;

/// <summary>The person's card, shown in the cabinet and at the top of the dashboard.</summary>
public sealed partial class ProfileCard : UserControl
{
    public ProfileCard() => InitializeComponent();

    /// <summary>
    /// Filled by hand rather than by binding.
    /// </summary>
    /// <remarks>
    /// A dozen values into a control created once. A set of bindings, a colour converter and a
    /// visibility converter would be more machinery than the thing they drive.
    /// </remarks>
    /// <remarks>
    /// Collapses itself when there is no profile yet, rather than drawing with blank fields. A
    /// first run before the welcome window — or one where it was skipped and the Windows account
    /// name failed validation, so nothing was saved — has no profile, and a card with an empty
    /// avatar letter and an empty "member since" line is exactly the half-finished look this
    /// control exists to remove. A collapsed <see cref="UserControl"/> takes no layout space, so
    /// the dashboard and the cabinet simply have nothing there instead.
    /// </remarks>
    public void Show(ProfileViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        if (!viewModel.HasProfile)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        CardName.Text = viewModel.Name;
        CardEmail.Text = viewModel.Email;
        EmailRow.Visibility = viewModel.Email.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CardSince.Text = viewModel.MemberSince;
        SinceRow.Visibility = viewModel.MemberSince.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AvatarInitial.Text = viewModel.Initial;

        // One instance for both shapes: the halo is softened by the Ellipse's own opacity, not by
        // a second brush carrying a different alpha.
        var colour = Brush(viewModel.Colour);
        AvatarCircle.Fill = colour;
        AvatarHalo.Fill = colour;

        ChangesValue.Text = viewModel.RecordedChangesValue;
        ChangesCaption.Text = viewModel.ChangesCaption;
        DaysValue.Text = viewModel.DaysWithWinora;
        DaysCaption.Text = viewModel.DaysCaption;

        // The journal is read after the profile, so the figures are briefly absent on a card that
        // is otherwise complete. Nothing is drawn for them until they arrive.
        var hasFigures = viewModel.RecordedChangesValue.Length > 0;
        StatsRule.Visibility = hasFigures ? Visibility.Visible : Visibility.Collapsed;
        Stats.Visibility = hasFigures ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// A brush from "#RRGGBB".
    /// </summary>
    /// <remarks>
    /// Falls back to a neutral slate grey rather than throwing: the colour arrives from a file a
    /// person could have edited, and a card is decoration — decoration must never be able to stop a
    /// screen from drawing.
    /// </remarks>
    private static SolidColorBrush Brush(string colour)
    {
        try
        {
            if (colour.Length == 7 && colour[0] == '#')
            {
                var red = Convert.ToByte(colour.Substring(1, 2), 16);
                var green = Convert.ToByte(colour.Substring(3, 2), 16);
                var blue = Convert.ToByte(colour.Substring(5, 2), 16);
                return new SolidColorBrush(ColorHelper.FromArgb(255, red, green, blue));
            }
        }
        catch (Exception)
        {
            // Not a colour. Fall through.
        }

        return new SolidColorBrush(Colors.SlateGray);
    }
}
