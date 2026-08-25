using Microsoft.UI.Xaml;
using Winora.App.Diagnostics;

namespace Winora.App.Services;

/// <summary>
/// Asks the person for a picture file.
/// </summary>
/// <remarks>
/// <para>
/// A file dialog has to be told which window owns it. There is no ambient answer to that in a
/// desktop WinUI app — the WinRT pickers were designed for a single-window model that this is not —
/// so a picker created without one throws at run time, not at build time, on the first click. Both
/// routes below are given the window explicitly for that reason.
/// </para>
/// <para>
/// The route that is tried first is the Windows App SDK's own picker,
/// <c>Microsoft.Windows.Storage.Pickers.FileOpenPicker</c>, which takes the window in its
/// constructor. It exists precisely because the older
/// <c>Windows.Storage.Pickers.FileOpenPicker</c> cannot be shown from an elevated process — it is
/// activated through a broker that refuses a high-integrity caller, and the call fails with an
/// access error after the button has already been pressed. Winora runs elevated by the owner's
/// decision (see AGENTS.md), so the older one is the fallback and not the default.
/// </para>
/// <para>
/// The fallback is kept rather than dropped because this cannot be checked here: an elevated window
/// admits neither screenshots nor UI automation, so which of the two actually opens the dialog on
/// this machine is something only a person sitting at it can see. Two routes and a log line beat
/// one route and a button that does nothing.
/// </para>
/// </remarks>
internal static class PicturePicker
{
    /// <summary>The three formats the checker will accept, so the dialog does not offer a fourth.</summary>
    /// <remarks>
    /// A convenience, never a defence. The dialog filters by extension and the extension is exactly
    /// what this program does not trust — <c>ProfilePictureRules</c> reads the bytes afterwards and
    /// has the last word.
    /// </remarks>
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];

    /// <summary>The chosen file's path, or null when the dialog was dismissed or could not open.</summary>
    public static async Task<string?> PickAsync(Window? owner)
    {
        if (owner is null)
        {
            return null;
        }

        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(owner.AppWindow.Id)
            {
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.Thumbnail,
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            };

            foreach (var extension in Extensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            var result = await picker.PickSingleFileAsync();

            return result?.Path;
        }
        catch (Exception ex)
        {
            DiagnosticSink.Write("PicturePicker.AppSdk", ex);
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            };

            // The step that is missed and then found at run time. Without it the picker has no
            // owner window and throws on the call below.
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(owner));

            foreach (var extension in Extensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            var file = await picker.PickSingleFileAsync();

            return file?.Path;
        }
        catch (Exception ex)
        {
            // Both routes refused. The screen says nothing, because nothing was chosen and nothing
            // was rejected — but the log says which, so a button that does nothing is diagnosable
            // rather than a mystery.
            DiagnosticSink.Write("PicturePicker.WinRT", ex);
            return null;
        }
    }
}
