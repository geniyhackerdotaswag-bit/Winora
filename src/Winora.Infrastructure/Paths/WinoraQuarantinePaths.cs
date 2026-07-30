namespace Winora.Infrastructure.Paths;

/// <summary>
/// Where quarantined items live. Deliberately outside <c>%LOCALAPPDATA%\Winora</c>.
/// </summary>
/// <remarks>
/// A packaged app's writes under <c>%LOCALAPPDATA%</c> are redirected into package storage, which
/// Windows deletes when the package is uninstalled. Quarantined items are the user's own files, so
/// putting them there would destroy data on uninstall without anyone being told. Measured on the
/// packaged build: writes under <c>%USERPROFILE%\Winora</c> reach the real path, so the quarantine
/// survives Winora being removed and stays reachable by hand.
/// </remarks>
public sealed class WinoraQuarantinePaths
{
    private WinoraQuarantinePaths(string rootDirectory)
    {
        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        QuarantineDirectory = Path.Combine(RootDirectory, "Quarantine");
    }

    /// <summary>The user-visible Winora folder, outside any package-scoped storage.</summary>
    public string RootDirectory { get; }

    public string QuarantineDirectory { get; }

    public static WinoraQuarantinePaths ForCurrentUser() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Winora"));

    public static WinoraQuarantinePaths ForRoot(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        return new WinoraQuarantinePaths(rootDirectory);
    }

    /// <summary>The directory holding one operation's quarantined items.</summary>
    public string DirectoryFor(string operationId) =>
        Path.Combine(QuarantineDirectory, ValidateSegment(operationId, nameof(operationId)));

    /// <summary>
    /// Resolves a destination inside an operation's quarantine and refuses anything that would land
    /// outside it, so a crafted relative path cannot walk out of the owned directory.
    /// </summary>
    public string ResolveOwnedPath(string operationId, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("A quarantine entry path must be relative.", nameof(relativePath));
        }

        var root = Path.TrimEndingDirectorySeparator(DirectoryFor(operationId));
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A quarantine entry must stay inside its operation directory.", nameof(relativePath));
        }

        return candidate;
    }

    /// <summary>
    /// Operation identifiers become directory names, so the same lower-case rule the rest of the
    /// data layout uses applies here, and reserved DOS device names are refused outright.
    /// </summary>
    private static string ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Length > 96 ||
            value.Any(static character =>
                character is not (>= 'a' and <= 'z') and
                    not (>= '0' and <= '9') and
                    not '-' and
                    not '_' and
                    not '.'))
        {
            throw new ArgumentException("A quarantine directory name must be a stable lower-case identifier.", parameterName);
        }

        var bare = value.Split('.')[0];
        string[] reserved =
        [
            "con", "prn", "aux", "nul",
            "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
            "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
        ];

        return reserved.Contains(bare, StringComparer.OrdinalIgnoreCase)
            ? throw new ArgumentException("A quarantine directory name must not be a reserved device name.", parameterName)
            : value;
    }
}
