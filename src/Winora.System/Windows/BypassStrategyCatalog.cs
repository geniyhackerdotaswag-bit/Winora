using System.Collections.Frozen;
using System.Security.Cryptography;
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
    /// Puts the lists a strategy references into the state it expects, before one is started.
    /// </summary>
    /// <returns>The files that had to be written, for the record.</returns>
    IReadOnlyList<string> PrepareLists();
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

    /// <summary>
    /// Strategies the release ships that Winora does not offer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner picked these out by name on 2026-08-14: the plain <c>general</c>, the experimental
    /// one, and the two families named after the trick they use — <c>FAKE TLS AUTO</c> and
    /// <c>SIMPLE FAKE</c>, each with its alternates. What is left is <c>ALT</c> through
    /// <c>ALT12</c>, twelve numbered variants that differ only in tuning.
    /// </para>
    /// <para>
    /// Hidden rather than deleted. The files stay where the release put them, so an upgrade does not
    /// have to put them back and nothing has to be reconciled with upstream; this list is the only
    /// place that knows they exist. Names are matched exactly, ignoring case, against the file name
    /// without its extension — the same string the strategy would have been called.
    /// </para>
    /// <para>
    /// A name here that upstream renames or drops simply stops matching, and that strategy would
    /// reappear in the list. That is the safe direction to fail: an unexpected extra entry is
    /// visible, a silently missing one is not.
    /// </para>
    /// </remarks>
    private static readonly FrozenSet<string> Hidden = new[]
    {
        "general",
        "general (EXP)",
        "general (FAKE TLS AUTO)",
        "general (FAKE TLS AUTO ALT)",
        "general (FAKE TLS AUTO ALT2)",
        "general (FAKE TLS AUTO ALT3)",
        "general (SIMPLE FAKE)",
        "general (SIMPLE FAKE ALT)",
        "general (SIMPLE FAKE ALT2)",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
            .Where(static file => !Hidden.Contains(Path.GetFileNameWithoutExtension(file)))
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

    /// <summary>The release's own host list, which Winora replaces with its own.</summary>
    private const string GeneralList = "list-general.txt";

    /// <summary>The name given to the embedded copy in <c>Winora.System.csproj</c>.</summary>
    private const string GeneralListResource = "Winora.System.list-general.txt";

    /// <summary>
    /// The host list Winora ships, read once out of the assembly.
    /// </summary>
    /// <remarks>
    /// Empty if it cannot be read, and an empty bundle writes nothing. That is the safe direction to
    /// fail: the release's own list stays where it is and the strategies keep working, rather than
    /// <c>winws.exe</c> being handed a truncated host list — which it treats as an error, exiting
    /// within a second with nothing on screen to explain it.
    /// </remarks>
    private static readonly Lazy<byte[]> BundledGeneralList = new(
        static () =>
        {
            try
            {
                using var stream = typeof(BypassStrategyCatalog).Assembly
                    .GetManifestResourceStream(GeneralListResource);

                if (stream is null)
                {
                    return [];
                }

                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }
            catch (Exception)
            {
                return [];
            }
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Puts the lists the launch line references into the state the strategies expect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two different jobs, and they differ in whether they overwrite.
    /// </para>
    /// <para>
    /// The three <c>*-user.txt</c> files are created only when missing, exactly as the release's own
    /// <c>service.bat :load_user_lists</c> would. This is the one side effect of the batch preamble
    /// that a strategy genuinely depends on, and leaving it out is why starting a strategy failed:
    /// Winora deliberately does not run those scripts — they also check for updates and print to a
    /// console — but every strategy passes these files to <c>--hostlist</c> and
    /// <c>--ipset-exclude</c>, and a missing host list is fatal to <c>winws.exe</c>. Measured on
    /// 2026-08-08 against release 1.10.0: all three were absent from a folder the installer had
    /// unpacked, and every strategy failed the same way. They are never rewritten — a user's own
    /// edits are theirs.
    /// </para>
    /// <para>
    /// <c>list-general.txt</c> is the opposite: it is always brought back to the copy Winora ships.
    /// The release's own is 902 bytes and covers little; the bundled one is around 150 000 hosts.
    /// Because the installer replaces this file on every upgrade, "write it once" would quietly
    /// revert the next time the release updated, so the check runs before every start.
    /// </para>
    /// <para>
    /// It is compared, not blindly written: length first, then SHA-256 only when the lengths match.
    /// Two megabytes is not worth writing to disk each time somebody presses start, and an unchanged
    /// file should stay untouched. Note this does overwrite an edit made to that one file by hand —
    /// which is what "always replace" means, and why the release keeps a separate
    /// <c>list-general-user.txt</c> for additions of one's own.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> PrepareLists()
    {
        var written = new List<string>();
        var lists = Path.Combine(RootDirectory, "lists");

        if (!Directory.Exists(lists))
        {
            return written;
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
                written.Add(name);
            }
            catch (Exception)
            {
                // A list that cannot be written will surface as a start that fails and reports why,
                // which is more useful than an exception out of a status refresh.
            }
        }

        if (ReplaceGeneralList(Path.Combine(lists, GeneralList)))
        {
            written.Add(GeneralList);
        }

        return written;
    }

    /// <summary>Writes the bundled host list over the release's, when they differ.</summary>
    /// <returns>True when the file was actually written.</returns>
    private static bool ReplaceGeneralList(string path)
    {
        var bundled = BundledGeneralList.Value;

        if (bundled.Length == 0)
        {
            return false;
        }

        try
        {
            if (Matches(path, bundled))
            {
                return false;
            }

            File.WriteAllBytes(path, bundled);
            return true;
        }
        catch (Exception)
        {
            // Same reasoning as above: a list that cannot be written shows up as a start that fails
            // and says so, which beats throwing out of a status refresh.
            return false;
        }
    }

    /// <summary>True when the file on disk already holds exactly these bytes.</summary>
    private static bool Matches(string path, byte[] bundled)
    {
        var file = new FileInfo(path);

        // Length settles it in every case but one, and costs nothing.
        if (!file.Exists || file.Length != bundled.Length)
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream).AsSpan().SequenceEqual(SHA256.HashData(bundled));
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
