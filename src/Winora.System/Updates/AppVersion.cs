namespace Winora.System.Updates;

/// <summary>Reads a version out of text that came from a build or from a git tag.</summary>
/// <remarks>
/// <para>
/// Everything is normalised to three numbers. <see cref="Version" /> stores an absent component as
/// -1, which makes <c>0.4</c> compare as less than <c>0.4.0</c>; a tag of <c>v0.4</c> against a
/// build called <c>0.4.0</c> would then look like an update forever, and installing it would change
/// nothing. Three numbers always, so the comparison means what it reads as.
/// </para>
/// <para>
/// A fourth number is dropped rather than kept. Releases are named with three, and a build that
/// carries a revision — which MSBuild adds on its own — must not read as newer than the release it
/// was built from.
/// </para>
/// </remarks>
public static class AppVersion
{
    /// <summary>The version in this text, or null when there is not one.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();

        // Tags are written v0.4.0; the version inside a build is not.
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        // AssemblyInformationalVersion carries "+<commit>" when SourceLink is on, and a pre-release
        // label after "-". Neither takes part in ordering here: releases are numbered, and a label
        // that changed the comparison would make the answer depend on how the build was tagged.
        var cut = value.IndexOfAny(['+', '-']);
        if (cut >= 0)
        {
            value = value[..cut];
        }

        if (!Version.TryParse(value, out var version))
        {
            return null;
        }

        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
