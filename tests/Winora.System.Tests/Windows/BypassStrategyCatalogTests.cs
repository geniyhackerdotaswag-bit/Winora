using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// Parsing a real release, not an invented example.
/// </summary>
/// <remarks>
/// The batch files come from someone else's project and their shape is theirs to change, so the
/// parser is only worth as much as the evidence it was tested against. These build a release layout
/// from files copied out of an actual download.
/// </remarks>
public sealed class BypassStrategyCatalogTests : IDisposable
{
    private readonly string _root;

    public BypassStrategyCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "winora-bypass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Directory.CreateDirectory(Path.Combine(_root, "lists"));
        Directory.CreateDirectory(Path.Combine(_root, "utils"));

        // Stands in for the real executable: the catalog only checks that it is there.
        File.WriteAllText(Path.Combine(_root, "bin", "winws.exe"), string.Empty);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a passing test over.
        }
    }

    /// <summary>The launch line as the release actually writes it, continuations and all.</summary>
    private const string RealStrategy = """
        @echo off
        chcp 65001 > nul
        cd /d "%~dp0"
        call service.bat load_game_filter

        set "BIN=%~dp0bin\"
        set "LISTS=%~dp0lists\"
        cd /d %BIN%

        start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=80,443,%GameFilterTCP% --wf-udp=443,%GameFilterUDP% ^
        --filter-udp=443 --hostlist="%LISTS%list-general.txt" --dpi-desync=fake --new ^
        --filter-tcp=80,443 --dpi-desync=multisplit --dpi-desync-split-pos=1
        """;

    /// <summary>
    /// The lists every strategy references, which the release's own script creates and Winora must
    /// therefore create too.
    /// </summary>
    /// <remarks>
    /// This is the defect that made every strategy fail on 2026-08-08. Winora deliberately does not
    /// run the batch preamble — it also checks for updates and writes to a console — but one of its
    /// four calls, <c>load_user_lists</c>, has a side effect the launch line depends on. Without
    /// these files <c>winws.exe</c> exits within a second and the screen goes back to "not running"
    /// with nothing anywhere to explain it.
    /// </remarks>
    [Theory]
    [InlineData("list-general-user.txt")]
    [InlineData("list-exclude-user.txt")]
    [InlineData("ipset-exclude-user.txt")]
    public void A_missing_user_list_is_created(string name)
    {
        var created = new BypassStrategyCatalog(_root).EnsureUserLists();

        var path = Path.Combine(_root, "lists", name);
        Assert.True(File.Exists(path), $"{name} was not created.");
        Assert.Contains(name, created);

        // The release seeds these with a placeholder rather than leaving them empty, and its own
        // comment says never to leave them empty — an empty host list is an error to winws.exe.
        Assert.NotEmpty(File.ReadAllText(path).Trim());
    }

    /// <summary>A user's own edits are theirs. An existing list is never rewritten.</summary>
    [Fact]
    public void An_existing_user_list_is_left_alone()
    {
        var path = Path.Combine(_root, "lists", "list-general-user.txt");
        File.WriteAllText(path, "mine.example");

        var created = new BypassStrategyCatalog(_root).EnsureUserLists();

        Assert.Equal("mine.example", File.ReadAllText(path));
        Assert.DoesNotContain("list-general-user.txt", created);
    }

    [Fact]
    public void Creating_the_user_lists_twice_changes_nothing_the_second_time()
    {
        var catalog = new BypassStrategyCatalog(_root);

        Assert.NotEmpty(catalog.EnsureUserLists());
        Assert.Empty(catalog.EnsureUserLists());
    }

    [Fact]
    public void A_strategy_is_read_from_its_batch_file()
    {
        Write("general.bat", RealStrategy);

        var strategy = Assert.Single(new BypassStrategyCatalog(_root).Strategies());

        Assert.Equal("general", strategy.Id);
        Assert.Equal(Path.Combine(_root, "bin", "winws.exe"), strategy.ExecutablePath);
        Assert.Equal(Path.Combine(_root, "bin"), strategy.WorkingDirectory);
    }

    /// <summary>
    /// The arguments span several lines joined by a trailing caret. Reading only the first line
    /// produces a strategy that is missing most of what makes it work — and it would still start,
    /// which is worse than not starting at all.
    /// </summary>
    [Fact]
    public void Arguments_continued_across_lines_are_all_collected()
    {
        Write("general.bat", RealStrategy);

        var arguments = new BypassStrategyCatalog(_root).Strategies().Single().Arguments;

        Assert.Contains("--wf-tcp=80,443,12", arguments);
        Assert.Contains("--filter-udp=443", arguments);
        Assert.Contains("--dpi-desync-split-pos=1", arguments);
        Assert.DoesNotContain(arguments, static argument => argument.Contains('^'));
    }

    /// <summary>
    /// Nothing may reach <c>winws.exe</c> still holding a batch variable: it would arrive as a
    /// literal "%LISTS%list-general.txt" and the strategy would silently do the wrong thing.
    /// </summary>
    [Fact]
    public void No_argument_is_left_holding_an_unexpanded_variable()
    {
        Write("general.bat", RealStrategy);

        foreach (var argument in new BypassStrategyCatalog(_root).Strategies().Single().Arguments)
        {
            Assert.DoesNotContain('%', argument);
        }
    }

    [Fact]
    public void List_paths_are_expanded_to_the_release_folder()
    {
        Write("general.bat", RealStrategy);

        var arguments = new BypassStrategyCatalog(_root).Strategies().Single().Arguments;

        Assert.Contains(
            arguments,
            argument => argument == "--hostlist=" + Path.Combine(_root, "lists", "list-general.txt"));
    }

    /// <summary>
    /// The game filter is computed by the release from a flag file, not written into the script.
    /// With the flag absent it is the harmless placeholder port; the parser must match that rather
    /// than pass the range through.
    /// </summary>
    [Fact]
    public void The_game_filter_is_off_when_the_release_says_so()
    {
        Write("general.bat", RealStrategy);

        var arguments = new BypassStrategyCatalog(_root).Strategies().Single().Arguments;

        Assert.Contains("--wf-tcp=80,443,12", arguments);
        Assert.Contains("--wf-udp=443,12", arguments);
    }

    [Fact]
    public void The_game_filter_widens_the_ports_when_the_release_enables_it()
    {
        Write("general.bat", RealStrategy);
        File.WriteAllText(Path.Combine(_root, "utils", "game_filter.enabled"), "enabled (TCP and UDP)");

        var arguments = new BypassStrategyCatalog(_root).Strategies().Single().Arguments;

        Assert.Contains("--wf-tcp=80,443,1024-65535", arguments);
        Assert.Contains("--wf-udp=443,1024-65535", arguments);
    }

    /// <summary>The release's own helper script is not a strategy and must never be offered.</summary>
    [Fact]
    public void The_helper_script_is_not_a_strategy()
    {
        Write("general.bat", RealStrategy);
        Write("service.bat", "@echo off\r\necho this is the helper, it also mentions winws.exe");

        Assert.Equal(["general"], new BypassStrategyCatalog(_root).Strategies().Select(s => s.Id));
    }

    /// <summary>
    /// Names are the file names, untouched. Someone told "use ALT3" on a forum has to find exactly
    /// that here, so this is the one list in the app where inventing nicer names would be wrong.
    /// </summary>
    [Fact]
    public void Names_are_the_release_names_unchanged()
    {
        Write("general (ALT3).bat", RealStrategy);
        Write("general (FAKE TLS AUTO).bat", RealStrategy);

        var names = new BypassStrategyCatalog(_root).Strategies().Select(static s => s.Name).ToArray();

        Assert.Contains("general (ALT3)", names);
        Assert.Contains("general (FAKE TLS AUTO)", names);
    }

    [Fact]
    public void A_batch_file_with_no_launch_line_is_skipped()
    {
        Write("general.bat", RealStrategy);
        Write("readme.bat", "@echo off\r\necho nothing to launch here");

        Assert.Equal(["general"], new BypassStrategyCatalog(_root).Strategies().Select(s => s.Id));
    }

    /// <summary>
    /// Without the executable there is nothing to run, so the screen must show an empty list and
    /// offer to download rather than list strategies that cannot start.
    /// </summary>
    [Fact]
    public void Nothing_is_offered_when_the_release_is_not_installed()
    {
        Write("general.bat", RealStrategy);
        File.Delete(Path.Combine(_root, "bin", "winws.exe"));

        var catalog = new BypassStrategyCatalog(_root);

        Assert.False(catalog.IsInstalled);
        Assert.Empty(catalog.Strategies());
    }

    [Fact]
    public void A_missing_folder_yields_nothing_rather_than_throwing()
    {
        var catalog = new BypassStrategyCatalog(Path.Combine(_root, "absent"));

        Assert.False(catalog.IsInstalled);
        Assert.Empty(catalog.Strategies());
    }

    /// <summary>The real store lives beside the cursor and sound folders, outside the container.</summary>
    [Fact]
    public void The_default_folder_is_under_the_user_profile()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Winora",
            "Zapret");

        Assert.Equal(expected, new BypassStrategyCatalog().RootDirectory);
    }

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_root, name), content);
}
