using Microsoft.UI.Xaml;
using Winora.App.Diagnostics;

namespace Winora.App.Services;

/// <summary>
/// The two dialogs the settings transfer needs: pick a file to read, name a file to write.
/// </summary>
/// <remarks>
/// Built on the Windows App SDK pickers for the same reason <see cref="PicturePicker"/> is: they
/// take the owning window's id, which is what makes a dialog appear over the window rather than
/// behind it or not at all. A dismissed dialog and a dialog that would not open are both null —
/// nothing was chosen either way, and the caller has nothing different to do about it.
/// </remarks>
public static class SettingsFilePicker
{
    /// <summary>The extension a settings file carries.</summary>
    public const string Extension = ".winora-settings";

    /// <summary>A file to read, or null when nothing was chosen.</summary>
    public static async Task<string?> OpenAsync(Window? owner)
    {
        if (owner is null)
        {
            return null;
        }

        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(owner.AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };

            picker.FileTypeFilter.Add(Extension);

            var result = await picker.PickSingleFileAsync();

            return result?.Path;
        }
        catch (Exception ex)
        {
            DiagnosticSink.Write("SettingsFilePicker.Open", ex);
            return null;
        }
    }

    /// <summary>A file to write, or null when nothing was chosen.</summary>
    public static async Task<string?> SaveAsync(Window? owner, string suggestedName)
    {
        if (owner is null)
        {
            return null;
        }

        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(owner.AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName,
                DefaultFileExtension = Extension,
            };

            picker.FileTypeChoices.Add("Winora", [Extension]);

            var result = await picker.PickSaveFileAsync();

            return result?.Path;
        }
        catch (Exception ex)
        {
            DiagnosticSink.Write("SettingsFilePicker.Save", ex);
            return null;
        }
    }
}
