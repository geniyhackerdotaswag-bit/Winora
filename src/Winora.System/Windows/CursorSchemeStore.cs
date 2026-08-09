using Microsoft.Win32;

namespace Winora.System.Windows;

/// <summary>One cursor role Windows can be asked to replace.</summary>
/// <remarks>
/// The order of these members is the order paths appear in a scheme string, and it is load-bearing.
/// </remarks>
public enum CursorRole
{
    Arrow,
    Help,
    AppStarting,
    Wait,
    Crosshair,
    IBeam,
    NWPen,
    No,
    SizeNS,
    SizeWE,
    SizeNWSE,
    SizeNESW,
    SizeAll,
    UpArrow,
    Hand,
    Pin,
    Person,
}

/// <param name="Name">The scheme name as Windows registered it.</param>
/// <param name="Files">Role to cursor file. Roles the scheme leaves blank are absent.</param>
/// <param name="IsMachineWide">True when it came from HKLM, which every account shares.</param>
public sealed record CursorScheme(
    string Name,
    IReadOnlyDictionary<CursorRole, string> Files,
    bool IsMachineWide);

/// <summary>Reads the cursor schemes installed on this machine. Never writes.</summary>
public interface ICursorSchemeStore
{
    IReadOnlyList<CursorScheme> Schemes();
}

/// <summary>
/// Reads cursor schemes from the two registry locations Windows keeps them in.
/// </summary>
/// <remarks>
/// <para>
/// Reading only. The scheme keys are not documented on Microsoft Learn, so Winora does not write
/// them and does not claim to: a scheme is applied through the documented
/// <c>LoadCursorFromFile</c> / <c>SetSystemCursor</c> pair instead, which needs no registry at all.
/// See <see cref="WindowsCursorApplier"/>.
/// </para>
/// <para>
/// The order of paths inside a scheme string is likewise undocumented, so it was measured rather
/// than assumed: on this machine the applied scheme appears both as an ordered string under
/// <c>Schemes</c> and as named values under <c>Control Panel\Cursors</c>, and matching the two gives
/// the mapping in <see cref="CursorRole"/>. Two positions contradict what the shipped file names
/// suggest — <c>diagonal_resize1</c> is <see cref="CursorRole.SizeNWSE"/> and <c>diagonal_resize2</c>
/// is <see cref="CursorRole.SizeNESW"/> — which is exactly why guessing was not acceptable: the two
/// diagonals would have been swapped on every pack.
/// </para>
/// <para>
/// A scheme with fewer entries than roles is read as far as it goes. Trailing roles are simply
/// absent, which is how Windows itself ships several schemes with no Pin or Person cursor.
/// </para>
/// </remarks>
public sealed class WindowsCursorSchemeStore : ICursorSchemeStore
{
    private const string UserSchemesKey = @"Control Panel\Cursors\Schemes";

    private const string MachineSchemesKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\Cursors\Schemes";

    public IReadOnlyList<CursorScheme> Schemes()
    {
        var schemes = new List<CursorScheme>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The user's own schemes win over a machine-wide one of the same name: that is the order
        // Windows resolves them in, and showing both would offer the same pack twice.
        Collect(Registry.CurrentUser, UserSchemesKey, isMachineWide: false, schemes, seen);
        Collect(Registry.LocalMachine, MachineSchemesKey, isMachineWide: true, schemes, seen);

        return schemes
            .OrderBy(static scheme => scheme.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Splits a scheme string into roles. Public because the ordering is the delicate part and is
    /// worth testing directly rather than only through the registry.
    /// </summary>
    public static IReadOnlyDictionary<CursorRole, string> ParseSchemeValue(string value)
    {
        var files = new Dictionary<CursorRole, string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return files;
        }

        var parts = value.Split(',');
        var roles = Enum.GetValues<CursorRole>();
        for (var index = 0; index < parts.Length && index < roles.Length; index++)
        {
            var path = parts[index].Trim();
            if (path.Length == 0)
            {
                // A blank position means "this scheme has no cursor for that role", not an error.
                continue;
            }

            files[roles[index]] = Environment.ExpandEnvironmentVariables(path);
        }

        return files;
    }

    private static void Collect(
        RegistryKey hive,
        string path,
        bool isMachineWide,
        List<CursorScheme> schemes,
        HashSet<string> seen)
    {
        try
        {
            using var key = hive.OpenSubKey(path, writable: false);
            if (key is null)
            {
                return;
            }

            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                {
                    continue;
                }

                if (key.GetValue(name) is not string value)
                {
                    continue;
                }

                var files = ParseSchemeValue(value);
                if (files.Count > 0)
                {
                    schemes.Add(new CursorScheme(name, files, isMachineWide));
                }
            }
        }
        catch (Exception)
        {
            // An unreadable hive yields no schemes rather than failing the screen.
        }
    }
}
