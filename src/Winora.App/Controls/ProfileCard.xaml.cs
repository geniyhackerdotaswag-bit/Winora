using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Winora.App.ViewModels;

namespace Winora.App.Controls;

/// <summary>The person's card, shown in the cabinet and at the top of the dashboard.</summary>
public sealed partial class ProfileCard : UserControl
{
    /// <summary>How wide the avatar is decoded, for a disc drawn at 76 points.</summary>
    /// <remarks>
    /// Room for a 200% display and no more. A four-megabyte photograph decoded at full size to be
    /// drawn inside a circle the width of a thumbnail is tens of megabytes of pixels nobody sees,
    /// and the card is built twice — the cabinet and the dashboard each hold one.
    /// </remarks>
    private const int AvatarDecodeWidth = 256;

    /// <summary>The same for the background, which spans the card.</summary>
    private const int BackgroundDecodeWidth = 1600;

    /// <summary>
    /// What is currently drawn, so the same file is not decoded again on the next keystroke.
    /// </summary>
    /// <remarks>
    /// The cabinet calls <see cref="Show"/> on every property change, which is how the card follows
    /// what somebody types into the name field. Without this, every letter typed would re-read two
    /// files off the disk and rebuild two bitmaps.
    /// </remarks>
    private string _avatarPath = string.Empty;

    private string _backgroundPath = string.Empty;

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
        CardSubscription.Text = viewModel.Subscription;
        SubscriptionRow.Visibility =
            viewModel.Subscription.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AvatarInitial.Text = viewModel.Initial;

        // The disc under everything else. A picture, when there is one, covers it; remove the
        // picture and the person's colour is there again without recomputing anything.
        var colour = Brush(viewModel.Colour);
        AvatarCircle.Fill = colour;

        ShowAvatarPicture(viewModel.AvatarImagePath);
        ShowBackgroundPicture(viewModel.BackgroundImagePath);

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
    /// The picture in the disc's place, or the drawn mark when there is not one.
    /// </summary>
    /// <remarks>
    /// Three things mean the drawn mark, and all three land in the same place: no picture chosen, a
    /// picture whose file is no longer there — which reaches here as an empty path, because the
    /// media store checked — and a picture that will not decode, which is only discovered after the
    /// bitmap has been handed over and comes back through ImageFailed.
    /// </remarks>
    private void ShowAvatarPicture(string path)
    {
        if (string.Equals(path, _avatarPath, StringComparison.Ordinal))
        {
            return;
        }

        // Recorded before the attempt, not after it. A file that fails to decode must not be tried
        // again on the next property change, and there are dozens of those while somebody types.
        _avatarPath = path;

        var bitmap = path.Length == 0 ? null : Load(path, AvatarDecodeWidth);

        if (bitmap is null)
        {
            DrawTheMark();
            return;
        }

        // Only if it is still the picture being asked for. A slow decode that fails after
        // somebody has already chosen a different file must not undo the newer one.
        bitmap.ImageFailed += (_, _) =>
        {
            if (string.Equals(path, _avatarPath, StringComparison.Ordinal))
            {
                DrawTheMark();
            }
        };

        AvatarPicture.Fill = new ImageBrush
        {
            ImageSource = bitmap,
            Stretch = Stretch.UniformToFill,
        };

        AvatarPicture.Visibility = Visibility.Visible;
        AvatarCircle.Visibility = Visibility.Collapsed;
        AvatarInitial.Visibility = Visibility.Collapsed;
    }

    /// <summary>The letter on its coloured disc, which is what a card looks like without a picture.</summary>
    private void DrawTheMark()
    {
        AvatarPicture.Visibility = Visibility.Collapsed;
        AvatarPicture.Fill = null;
        AvatarCircle.Visibility = Visibility.Visible;
        AvatarInitial.Visibility = Visibility.Visible;
    }

    /// <summary>The picture behind the card's contents, under its scrim.</summary>
    private void ShowBackgroundPicture(string path)
    {
        if (string.Equals(path, _backgroundPath, StringComparison.Ordinal))
        {
            return;
        }

        _backgroundPath = path;

        var bitmap = path.Length == 0 ? null : Load(path, BackgroundDecodeWidth);

        if (bitmap is null)
        {
            ClearBackground();
            return;
        }

        bitmap.ImageFailed += (_, _) =>
        {
            if (string.Equals(path, _backgroundPath, StringComparison.Ordinal))
            {
                ClearBackground();
            }
        };

        Backdrop.Background = new ImageBrush
        {
            ImageSource = bitmap,
            Stretch = Stretch.UniformToFill,
        };

        Backdrop.Visibility = Visibility.Visible;

        // The scrim exists only over a picture. Left up on a plain card it would be an extra
        // fourteen per cent of the card's own colour over itself, which is a visible seam.
        BackdropScrim.Visibility = Visibility.Visible;
    }

    private void ClearBackground()
    {
        Backdrop.Visibility = Visibility.Collapsed;
        Backdrop.Background = null;
        BackdropScrim.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// A bitmap from a file this program wrote.
    /// </summary>
    /// <remarks>
    /// Read through the file system and handed over as a stream rather than given a URI to fetch
    /// for itself. A packaged app's image loader resolves <c>file:</c> through the storage broker
    /// and can be refused where plain file reads are not — and plain file reads are how everything
    /// else in Winora's store is already read, so this route is known to work wherever the rest of
    /// the profile does. The stream is a MemoryStream over bytes already in hand, so nothing is
    /// left open behind the decode.
    /// </remarks>
    private static BitmapImage? Load(string path, int decodeWidth)
    {
        try
        {
            var stream = new MemoryStream(File.ReadAllBytes(path));

            // Set before the source, or it is ignored: the decode has already been configured by
            // the time the bytes arrive.
            var bitmap = new BitmapImage { DecodePixelWidth = decodeWidth };

            bitmap.SetSource(stream.AsRandomAccessStream());

            return bitmap;
        }
        catch (Exception)
        {
            // Gone between the check and here, locked, or not a picture after all. The card falls
            // back to the drawn mark, which is what it looked like yesterday.
            return null;
        }
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
