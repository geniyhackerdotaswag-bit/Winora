namespace Winora.App.Services;

/// <summary>
/// Paths as they are shown on screen.
/// </summary>
/// <remarks>
/// <para>
/// The user profile is folded back to <c>%USERPROFILE%</c>. Every path Winora displays sits under
/// it, and the only part that differs between machines is the account name — which is a real name
/// often enough that a screenshot of a Winora screen was, until now, a screenshot of who the user
/// is. Nothing is hidden by this: the folder buttons still open the real location, and the variable
/// resolves for anyone who pastes it into Explorer or a shell.
/// </para>
/// <para>
/// Display only. Nothing derived from these strings is ever opened, written to, or passed to a
/// process — those all take the real path from Winora's own configuration.
/// </para>
/// </remarks>
public static class PathDisplay
{
    /// <summary>Folds the current user's profile directory back to its variable.</summary>
    public static string Redact(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var profile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);

        if (string.IsNullOrEmpty(profile))
        {
            return path;
        }

        profile = Path.TrimEndingDirectorySeparator(profile);

        // Only a whole leading segment. Matching a bare substring would mangle an unrelated path
        // that merely began with the same characters.
        if (path.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return "%USERPROFILE%" + path[profile.Length..];
        }

        return string.Equals(path, profile, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%"
            : path;
    }
}
