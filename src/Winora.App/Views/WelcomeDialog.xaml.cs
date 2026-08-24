using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winora.App.Services;
using Winora.App.ViewModels;

namespace Winora.App.Views;

/// <summary>The one window a person sees before anything else, once.</summary>
/// <remarks>
/// A greeting, not a gate. It can be skipped with one press, and skipping still leaves a profile —
/// otherwise the window would return on every launch, which is how a greeting becomes a nuisance.
/// </remarks>
public sealed partial class WelcomeDialog : ContentDialog
{
    private readonly ProfileViewModel _profile;

    private WelcomeDialog(ProfileViewModel profile, ILocalizationService text)
    {
        _profile = profile;
        InitializeComponent();

        Title = text.Get("Welcome_Title");
        PrimaryButtonText = text.Get("Welcome_Start");
        CloseButtonText = text.Get("Welcome_Skip");
        DefaultButton = ContentDialogButton.Primary;

        AboutText.Text = text.Get("Welcome_About");
        SafetyText.Text = text.Get("App_Safety_Statement");
        NameLabel.Text = text.Get("Profile_NameLabel");
        EmailLabel.Text = text.Get("Profile_EmailLabel");
        PrivacyText.Text = text.Get("Profile_EmailPrivacy");

        NameBox.PlaceholderText = text.Get("Welcome_NameHint");
        NameBox.Text = profile.Name;

        // "Начать" is disabled until the name and email both pass the same rules the save itself
        // enforces — otherwise pressing it silently does nothing, which looks like the button (or
        // the whole dialog) is broken rather than like a rejected form.
        NameBox.TextChanged += (_, _) => UpdatePrimaryButton();
        EmailBox.TextChanged += (_, _) => UpdatePrimaryButton();
        UpdatePrimaryButton();
    }

    /// <summary>Reflects the current field contents on the button that submits them.</summary>
    private void UpdatePrimaryButton()
    {
        _profile.Name = NameBox.Text;
        _profile.Email = EmailBox.Text;
        IsPrimaryButtonEnabled = _profile.CanSave;
    }

    /// <summary>Shows the greeting when nobody has introduced themselves yet.</summary>
    public static async Task ShowIfNeededAsync(XamlRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var profile = App.Services.GetRequiredService<ProfileViewModel>();
        profile.Load();

        if (profile.HasProfile)
        {
            return;
        }

        var dialog = new WelcomeDialog(
            profile,
            App.Services.GetRequiredService<ILocalizationService>())
        {
            XamlRoot = root,
        };

        var pressed = await dialog.ShowAsync();

        // The decision itself — what to save, given how the dialog closed — is pure and lives in
        // WelcomeOutcome, where it can be covered without touching this WinUI type at all.
        var outcome = WelcomeOutcome.Resolve(
            pressed == ContentDialogResult.Primary,
            dialog.NameBox.Text,
            dialog.EmailBox.Text,
            App.Services.GetRequiredService<IProfileService>().SuggestedName);

        // Skipping still writes a profile, so the greeting does not come back every launch. If the
        // Windows account name will not pass the rules — which would take an empty one — nothing is
        // written and the person is asked again next time. That is the honest outcome either way.
        if (outcome is { } saved)
        {
            profile.Name = saved.Name;
            profile.Email = saved.Email;
            profile.SaveCommand.Execute(null);
        }
    }
}
