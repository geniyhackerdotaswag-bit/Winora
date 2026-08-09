namespace Winora.System.Windows;

/// <param name="Name">The folder name, used as the pack name.</param>
/// <param name="Directory">Absolute path to the pack folder.</param>
/// <param name="Files">Role to cursor file, for roles that could be identified.</param>
/// <param name="UnmatchedFileNames">Cursor files whose role could not be identified.</param>
/// <param name="ImagePath">The pack's own preview image, or empty when it ships none.</param>
public sealed record CursorFolderPack(
    string Name,
    string Directory,
    IReadOnlyDictionary<CursorRole, string> Files,
    IReadOnlyList<string> UnmatchedFileNames,
    string ImagePath);

/// <summary>Reads cursor packs the user has dropped into a folder.</summary>
public interface ICursorFolderScanner
{
    /// <summary>The folder packs are read from. Created on demand so the user can find it.</summary>
    string RootDirectory { get; }

    IReadOnlyList<CursorFolderPack> Packs();
}

/// <summary>
/// Reads cursor packs from a folder the user fills themselves.
/// </summary>
/// <remarks>
/// <para>
/// Winora does not download packs. This is the agreed middle ground: the user vets what goes in the
/// folder, and the app only ever reads from it.
/// </para>
/// <para>
/// Only <c>.cur</c> and <c>.ani</c> are read. Everything else is ignored on purpose, and the
/// exclusion of <c>install.inf</c> is the whole point: that file is what makes a downloaded pack
/// dangerous, because installing it hands Windows a list of commands from a stranger. Winora will
/// not open it, parse it, or act on it, and it runs elevated, which makes that restraint matter more
/// rather than less.
/// </para>
/// <para>
/// Roles are matched by file name, which is a guess — pack authors name files however they like.
/// The guess is therefore made visible rather than hidden: a file that matches nothing is reported
/// in <see cref="CursorFolderPack.UnmatchedFileNames"/>, that role keeps whatever cursor it has, and
/// the screen shows how many roles were actually recognised before anything is applied.
/// </para>
/// </remarks>
public sealed class CursorFolderScanner : ICursorFolderScanner
{
    /// <summary>
    /// Ordered longest-distinctive-first. "no" would otherwise match "normal", and "up" would match
    /// "unsupported"; matching the more specific token first is what keeps those apart.
    /// </summary>
    private static readonly (string Token, CursorRole Role)[] Tokens =
    [
        // The stem, not "working": packs ship both spellings and one token covers each.
        ("work", CursorRole.AppStarting),
        ("appstarting", CursorRole.AppStarting),
        ("background", CursorRole.AppStarting),
        ("normal", CursorRole.Arrow),
        ("default", CursorRole.Arrow),
        ("pointer", CursorRole.Arrow),
        ("arrow", CursorRole.Arrow),
        ("help", CursorRole.Help),
        ("busy", CursorRole.Wait),
        ("wait", CursorRole.Wait),
        ("precision", CursorRole.Crosshair),
        ("cross", CursorRole.Crosshair),
        ("text", CursorRole.IBeam),
        ("beam", CursorRole.IBeam),
        ("handwriting", CursorRole.NWPen),
        ("pen", CursorRole.NWPen),

        // Truncated on purpose: "unavailiable" is a common misspelling in real packs, and the stem
        // matches it as well as the correct word without listing every way to get it wrong.
        ("unavail", CursorRole.No),
        // Stems again, so "vertical"/"vert" and "horizontal"/"horz" each need only one entry.
        ("vert", CursorRole.SizeNS),
        ("horizontal", CursorRole.SizeWE),
        ("horz", CursorRole.SizeWE),
        ("diagonal1", CursorRole.SizeNWSE),
        ("diagonal2", CursorRole.SizeNESW),
        ("dg1", CursorRole.SizeNWSE),
        ("dg2", CursorRole.SizeNESW),
        ("nwse", CursorRole.SizeNWSE),
        ("nesw", CursorRole.SizeNESW),
        ("move", CursorRole.SizeAll),
        ("alternate", CursorRole.UpArrow),
        ("link", CursorRole.Hand),
        ("hand", CursorRole.Hand),
        ("diagonal", CursorRole.SizeNWSE),

        // Short and ambiguous, so they come last and only win when nothing above matched.
        ("no", CursorRole.No),
        ("up", CursorRole.UpArrow),
    ];

    private static readonly string[] CursorExtensions = [".cur", ".ani"];

    /// <summary>
    /// Packs routinely ship their own preview picture. Showing it beats a rendered pointer: it is
    /// what the author chose to represent the set, and it survives a pack whose arrow cursor cannot
    /// be identified by name.
    /// </summary>
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];

    private readonly IArchiveExtractor _extractor;

    public CursorFolderScanner()
        : this(DefaultRoot(), new ArchiveExtractor(".cur", ".ani"))
    {
    }

    public CursorFolderScanner(string rootDirectory)
        : this(rootDirectory, new ArchiveExtractor(".cur", ".ani"))
    {
    }

    public CursorFolderScanner(string rootDirectory, IArchiveExtractor extractor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = rootDirectory;
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    }

    public string RootDirectory { get; }

    /// <summary>
    /// Under the user profile rather than app-local storage: a packaged app's local folder is
    /// redirected somewhere the user cannot reasonably find, and a folder nobody can find is a
    /// folder nobody will drop packs into.
    /// </summary>
    private static string DefaultRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Winora",
            "Cursors");

    public IReadOnlyList<CursorFolderPack> Packs()
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
        }
        catch (Exception)
        {
            return [];
        }

        // Packs arrive as archives. Unpacking first is what makes a folder of untouched .zip files
        // show anything at all; already-extracted ones are left alone.
        _extractor.ExtractPending(RootDirectory);

        var packs = new List<CursorFolderPack>();
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(RootDirectory);
        }
        catch (Exception)
        {
            return [];
        }

        foreach (var directory in directories)
        {
            packs.AddRange(TryRead(directory));
        }

        return packs
            .OrderBy(static pack => pack.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Resolves a cursor file name to a role, or null when nothing matches confidently.
    /// Public because the matching is the guess in this class and deserves direct testing.
    /// </summary>
    public static CursorRole? RoleForFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

        // The diagonals are told apart by a trailing number, and packs write it every way there is:
        // diagonal1, diagonal_resize1, "diagonal resize 1". A fixed token missed the common form,
        // which a test caught before it could swap the two arrows on every pack.
        if (name.Contains("diagonal", StringComparison.Ordinal))
        {
            return name.Contains('2') ? CursorRole.SizeNESW : CursorRole.SizeNWSE;
        }

        foreach (var (token, role) in Tokens)
        {
            if (name.Contains(token, StringComparison.Ordinal))
            {
                return role;
            }
        }

        return null;
    }

    private static IReadOnlyList<CursorFolderPack> TryRead(string directory)
    {
        string[] files;
        try
        {
            // Searched recursively: an archive unpacks into a subfolder of its own, and packs are
            // routinely distributed with the cursors one level down rather than at the top.
            files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(static file => CursorExtensions.Contains(
                    Path.GetExtension(file),
                    StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }

        if (files.Length == 0)
        {
            return [];
        }

        // A single download often holds several complete sets, marked by a bracket tag on every
        // file: [PREMIUM], [IR], [TB]. Treating them as one pack would mix cursors from different
        // designs into a single scheme, so each tag becomes its own pack.
        var groups = files
            .GroupBy(static file => SetTagOf(Path.GetFileName(file)), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var packName = CursorPackNaming.Clean(NameSourceFor(directory, files));

        string image;
        try
        {
            image = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .FirstOrDefault(static file => ImageExtensions.Contains(
                    Path.GetExtension(file),
                    StringComparer.OrdinalIgnoreCase)) ?? string.Empty;
        }
        catch (Exception)
        {
            image = string.Empty;
        }
        var splitBySet = groups.Count(static group => group.Key.Length > 0) > 1;

        var packs = new List<CursorFolderPack>();
        foreach (var group in groups)
        {
            var pack = Build(
                splitBySet ? CursorPackNaming.Combine(packName, group.Key) : packName,
                directory,
                group.ToArray(),
                image);
            if (pack is not null)
            {
                packs.Add(pack);
            }
        }

        return packs;
    }

    private static CursorFolderPack? Build(string name, string directory, string[] files, string image)
    {
        var candidates = new Dictionary<CursorRole, List<string>>();
        var unmatched = new List<string>();

        foreach (var file in files)
        {
            if (RoleForFileName(file) is { } role)
            {
                if (!candidates.TryGetValue(role, out var list))
                {
                    list = [];
                    candidates[role] = list;
                }

                list.Add(file);
            }
            else
            {
                unmatched.Add(Path.GetFileName(file));
            }
        }

        // Packs offer alternatives for a role — "Busy", "Busy - Orbs", "Busy - Time". The shortest
        // name is the plain variant, and picking it makes the choice deterministic instead of
        // depending on the order the file system happened to return.
        var matched = candidates.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value
                .OrderBy(static file => Path.GetFileName(file).Length)
                .ThenBy(static file => file, StringComparer.Ordinal)
                .First());

        return matched.Count == 0
            ? null
            : new CursorFolderPack(name, directory, matched, unmatched, image);
    }

    /// <summary>
    /// The bracket tag a pack uses to separate its sets, or empty when a file carries none.
    /// </summary>
    public static string SetTagOf(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (fileName.Length > 2 && fileName[0] == '[')
        {
            var close = fileName.IndexOf(']', 1);
            if (close > 1)
            {
                return fileName[1..close].Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Prefers the name of the single archive or single subfolder inside a pack folder: a user's
    /// own folder is often called "вариант2" while the download inside says what it actually is.
    /// </summary>
    private static string NameSourceFor(string directory, IReadOnlyList<string> cursorFiles)
    {
        var folderName = Path.GetFileName(directory);

        try
        {
            var archives = Directory.GetFiles(directory, "*.zip");
            if (archives.Length == 1)
            {
                return Path.GetFileNameWithoutExtension(archives[0]);
            }

            var children = Directory.GetDirectories(directory);
            if (children.Length == 1 && Directory.GetFiles(directory).Length == 0)
            {
                return Path.GetFileName(children[0]);
            }

            // A folder called "123" or "вариант1" says nothing, so the pack names itself: first
            // from a prefix its files share, then from the single subfolder it unpacked into.
            // Derived from what is on disk rather than invented, so the user can see where it
            // came from — and renaming the folder still wins.
            if (CursorPackNaming.IsUninformative(folderName))
            {
                var prefix = CursorPackNaming.CommonPrefixOf(
                    cursorFiles.Select(static file => Path.GetFileName(file)).ToArray());
                if (prefix.Length > 0)
                {
                    return prefix;
                }

                if (children.Length == 1)
                {
                    return Path.GetFileName(children[0]);
                }
            }
        }
        catch (Exception)
        {
        }

        return folderName;
    }
}
