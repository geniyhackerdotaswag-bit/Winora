using System.Text;

namespace Winora.System.Windows;

/// <param name="Id">Stable identifier: the batch file name without its extension.</param>
/// <param name="Name">
/// What to show. Identical to <paramref name="Id" /> on purpose — see the catalog remarks.
/// </param>
/// <param name="ExecutablePath">Full path to <c>winws.exe</c>.</param>
/// <param name="Arguments">Fully expanded arguments, ready to pass to the process.</param>
/// <param name="WorkingDirectory">Where to start it, which its relative paths depend on.</param>
public sealed record BypassStrategy(
    string Id,
    string Name,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

/// <summary>Finds the bypass strategies in an unpacked release.</summary>
public interface IBypassStrategyCatalog
{
    /// <summary>Where the release is unpacked.</summary>
    string RootDirectory { get; }

    /// <summary>True when a usable release is present.</summary>
    bool IsInstalled { get; }

    IReadOnlyList<BypassStrategy> Strategies();

    /// <summary>
    /// Creates the user-editable lists a strategy references, when they are missing.
    /// </summary>
    /// <returns>The files that had to be created, for the record.</returns>
    IReadOnlyList<string> EnsureUserLists();
}

/// <summary>
/// Reads the strategies out of a <c>zapret-discord-youtube</c> release.
/// </summary>
/// <remarks>
/// <para>
/// The release ships one batch file per strategy, each ending in a <c>start … winws.exe …</c> line
/// with the arguments that make that strategy what it is. Winora does not run the batch files: it
/// reads the arguments out of them and starts <c>winws.exe</c> itself, so nothing else in the script
/// — update checks, console output, window titles — runs on the user's machine.
/// </para>
/// <para>
/// Names are the file names, unchanged and untranslated. That is deliberate and the opposite of what
/// the cursor packs do: a person reading a forum thread that says "try ALT3" has to find exactly
/// <c>ALT3</c> here. The name is an identifier, not a label.
/// </para>
/// <para>
/// Everything here reads files and parses text. Nothing is executed.
/// </para>
/// </remarks>
public sealed class BypassStrategyCatalog : IBypassStrategyCatalog
{
    /// <summary>The release's own helper script, not a strategy.</summary>
    private const string HelperScript = "service.bat";

    private const string Executable = "winws.exe";

    public BypassStrategyCatalog()
        : this(DefaultRoot())
    {
    }

    public BypassStrategyCatalog(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
    }

    public string RootDirectory { get; }

    /// <remarks>
    /// Beside the cursor and sound folders, and outside the package container for the same reason:
    /// a packaged app's own storage is deleted when the package is removed, and re-downloading a
    /// release on every reinstall would be rude.
    /// </remarks>
    private static string DefaultRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Winora",
            "Zapret");

    public bool IsInstalled => File.Exists(ExecutablePath());

    public IReadOnlyList<BypassStrategy> Strategies()
    {
        if (!IsInstalled)
        {
            return [];
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(RootDirectory, "*.bat", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            return [];
        }

        var variables = Variables();

        return files
            .Where(static file => !string.Equals(
                Path.GetFileName(file), HelperScript, StringComparison.OrdinalIgnoreCase))
            .Select(file => Read(file, variables))
            .Where(static strategy => strategy is not null)
            .Select(static strategy => strategy!)
            .OrderBy(static strategy => strategy.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The user-editable lists, and the contents the release seeds them with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Copied from <c>service.bat :load_user_lists</c>, including the placeholder domain. That is
    /// not decoration: the release's own comment in that routine says never to leave the file empty,
    /// and <c>winws.exe</c> treats an empty host list as an error.
    /// </para>
    /// <para>
    /// The excluded address is in <c>203.0.113.0/24</c>, the block RFC 5737 reserves for
    /// documentation. It routes nowhere, which is why the release picked it.
    /// </para>
    /// </remarks>
    private static readonly (string Name, string[] Lines)[] UserLists =
    [
        ("ipset-exclude-user.txt", ["203.0.113.113/32"]),
        ("list-general-user.txt", ["# Never leave this file empty", "domain.example.abc"]),
        ("list-exclude-user.txt", ["domain.example.abc"]),
    ];

    /// <summary>
    /// Creates the user lists the launch line references, exactly as the release's own script would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one side effect of the batch preamble that a strategy genuinely depends on, and
    /// leaving it out is why starting a strategy failed. Winora deliberately does not run those
    /// scripts — they also check for updates and print to a console — but three of the four calls in
    /// the preamble are cosmetic and the fourth, <c>load_user_lists</c>, creates files that every
    /// strategy then passes to <c>--hostlist</c> and <c>--ipset-exclude</c>. A missing host list is
    /// fatal to <c>winws.exe</c>, so the process started and exited within a second, and the screen
    /// simply went back to "not running" with nothing to explain it.
    /// </para>
    /// <para>
    /// Measured on 2026-08-08 against release 1.10.0: all three files were absent from a folder the
    /// installer had unpacked, and every strategy failed the same way.
    /// </para>
    /// <para>
    /// Only these three fixed names are ever written, always inside the release's own
    /// <c>lists</c> folder, and never over a file that already exists — a user's own edits are
    /// theirs.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> EnsureUserLists()
    {
        var created = new List<string>();
        var lists = Path.Combine(RootDirectory, "lists");

        if (!Directory.Exists(lists))
        {
            return created;
        }

        foreach (var (name, lines) in UserLists)
        {
            var path = Path.Combine(lists, name);
            try
            {
                if (File.Exists(path))
                {
                    continue;
                }

                File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                created.Add(name);
            }
            catch (Exception)
            {
                // A list that cannot be written will surface as a start that fails and reports why,
                // which is more useful than an exception out of a status refresh.
            }
        }

        return created;
    }

    private string ExecutablePath() => Path.Combine(RootDirectory, "bin", Executable);

    private BypassStrategy? Read(string batFile, IReadOnlyDictionary<string, string> variables)
    {
        try
        {
            var command = CommandLine(File.ReadAllLines(batFile, Encoding.UTF8));
            if (command is null)
            {
                return null;
            }

            var arguments = Tokenize(Expand(command, variables))
                // Everything up to and including the executable is the "start" wrapper, which
                // Winora does not reproduce: it launches the process directly.
                .SkipWhile(static token => !token.EndsWith(Executable, StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .ToArray();

            if (arguments.Length == 0)
            {
                return null;
            }

            var name = Path.GetFileNameWithoutExtension(batFile);

            return new BypassStrategy(
                name,
                name,
                ExecutablePath(),
                arguments,
                Path.Combine(RootDirectory, "bin"));
        }
        catch (Exception)
        {
            // A file that cannot be read or does not hold a launch line is skipped rather than
            // shown as a strategy that would fail when chosen.
            return null;
        }
    }

    /// <summary>
    /// Joins the launch line and the continuation lines that follow it.
    /// </summary>
    /// <remarks>
    /// The arguments span a dozen lines held together by a trailing <c>^</c>. Reading only the first
    /// line would produce a strategy missing most of what makes it work, and it would still start —
    /// which is worse than not starting at all.
    /// </remarks>
    private static string? CommandLine(IReadOnlyList<string> lines)
    {
        var start = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Contains(Executable, StringComparison.OrdinalIgnoreCase))
            {
                start = index;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        for (var index = start; index < lines.Count; index++)
        {
            var line = lines[index].TrimEnd();
            var continues = line.EndsWith('^');

            builder.Append(continues ? line[..^1] : line).Append(' ');

            if (!continues)
            {
                break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The batch variables the launch line uses.
    /// </summary>
    /// <remarks>
    /// <c>GameFilterTCP</c> and <c>GameFilterUDP</c> are not literals in the script: the release
    /// computes them from <c>utils\game_filter.enabled</c>, and with the filter off they become the
    /// port "12" — a deliberately harmless placeholder. Leaving them unexpanded would hand
    /// <c>winws.exe</c> a literal "%GameFilterTCP%" as a port list.
    /// </remarks>
    private IReadOnlyDictionary<string, string> Variables()
    {
        var root = RootDirectory + Path.DirectorySeparatorChar;
        var (tcp, udp) = GameFilter();

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["~dp0"] = root,
            ["BIN"] = Path.Combine(RootDirectory, "bin") + Path.DirectorySeparatorChar,
            ["LISTS"] = Path.Combine(RootDirectory, "lists") + Path.DirectorySeparatorChar,
            ["GameFilter"] = tcp,
            ["GameFilterTCP"] = tcp,
            ["GameFilterUDP"] = udp,
        };
    }

    private (string Tcp, string Udp) GameFilter()
    {
        const string Off = "12";
        const string On = "1024-65535";

        try
        {
            var flag = Path.Combine(RootDirectory, "utils", "game_filter.enabled");
            if (!File.Exists(flag))
            {
                return (Off, Off);
            }

            var mode = File.ReadAllText(flag).Trim();

            // The file names which side is on. Anything unrecognised is treated as off, because a
            // wider port range than the user asked for is the more surprising direction to guess.
            var tcp = mode.Contains("TCP", StringComparison.OrdinalIgnoreCase) ? On : Off;
            var udp = mode.Contains("UDP", StringComparison.OrdinalIgnoreCase) ? On : Off;

            return tcp == Off && udp == Off ? (Off, Off) : (tcp, udp);
        }
        catch (Exception)
        {
            return (Off, Off);
        }
    }

    private static string Expand(string command, IReadOnlyDictionary<string, string> variables)
    {
        foreach (var (name, value) in variables)
        {
            command = command.Replace($"%{name}%", value, StringComparison.OrdinalIgnoreCase);
        }

        return command;
    }

    /// <summary>
    /// Splits a command line on spaces, keeping quoted runs together.
    /// </summary>
    /// <remarks>
    /// The quotes are removed: they exist so the shell keeps a path with spaces in one piece, and
    /// passing them through would make the argument include literal quote characters.
    /// </remarks>
    private static IEnumerable<string> Tokenize(string command)
    {
        var token = new StringBuilder();
        var quoted = false;

        foreach (var character in command)
        {
            switch (character)
            {
                case '"':
                    quoted = !quoted;
                    break;

                case ' ' when !quoted:
                    if (token.Length > 0)
                    {
                        yield return token.ToString();
                        token.Clear();
                    }

                    break;

                default:
                    token.Append(character);
                    break;
            }
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }
}
