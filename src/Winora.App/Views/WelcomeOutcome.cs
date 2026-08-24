namespace Winora.App.Views;

/// <summary>
/// The pure part of "what happens when the welcome dialog closes": given how it closed, what was
/// typed, and what Windows would suggest, decides what to save — or that nothing passes the rules
/// well enough to write at all.
/// </summary>
/// <remarks>
/// Deliberately apart from <see cref="WelcomeDialog"/>. That class extends
/// <c>Microsoft.UI.Xaml.Controls.ContentDialog</c>, and touching any type in the Winora.App
/// assembly from a plain xUnit host trips the CsWinRT module initializer and fails with
/// REGDB_E_CLASSNOTREG outside a packaged WinUI process. This type has no WinUI dependency at all,
/// so it can be linked into Winora.App.Tests as source — the same way ProfileViewModel and
/// UpdateViewModel already are — and covered directly.
/// </remarks>
public static class WelcomeOutcome
{
    /// <summary>
    /// What ends up saved: the resolved name and email, or <see langword="null"/> when the result
    /// does not pass <see cref="Winora.Core.Profile.ProfileRules"/>.
    /// </summary>
    /// <param name="startPressed">Whether "Начать" was pressed, as opposed to "Пропустить".</param>
    /// <param name="typedName">What was in the name field when the dialog closed.</param>
    /// <param name="typedEmail">What was in the email field when the dialog closed.</param>
    /// <param name="suggestedName">The Windows account name, used only when skipping.</param>
    public static (string Name, string Email)? Resolve(
        bool startPressed,
        string typedName,
        string typedEmail,
        string suggestedName)
    {
        // Skipping never keeps typed text: a person who pressed "Пропустить" did not mean to
        // submit whatever was sitting in the fields. The Windows account name stands in instead,
        // and the email is left blank rather than guessed at.
        var name = startPressed ? typedName : suggestedName;
        var email = startPressed ? typedEmail : string.Empty;

        return Winora.Core.Profile.ProfileRules.IsNameValid(name) &&
               Winora.Core.Profile.ProfileRules.IsEmailValid(email)
            ? (name, email)
            : null;
    }
}
