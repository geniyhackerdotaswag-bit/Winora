namespace Winora.System.Windows;

/// <summary>
/// Immutable read-only description of the running Windows version. Winora never mutates the
/// operating system version and never infers capability from a marketing name; only the
/// documented major/minor/build triple participates in support decisions.
/// </summary>
public sealed record WindowsBuildFacts
{
    public WindowsBuildFacts(int major, int minor, int build)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(build);

        Major = major;
        Minor = minor;
        Build = build;
    }

    /// <summary>
    /// The oldest Windows 11 build Winora supports. Everything older is reported as unsupported
    /// instead of being probed for direct mutation.
    /// </summary>
    public static WindowsBuildFacts Windows11Baseline { get; } = new(10, 0, 22000);

    public int Major { get; }

    public int Minor { get; }

    public int Build { get; }

    /// <summary>
    /// Compares major, then minor, then build. A newer major or minor always satisfies an older
    /// minimum even when the build number is smaller, because build numbers restart per branch.
    /// </summary>
    public bool MeetsMinimum(int major, int minor, int build)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(build);

        if (Major != major)
        {
            return Major > major;
        }

        return Minor != minor ? Minor > minor : Build >= build;
    }

    public bool MeetsMinimum(WindowsBuildFacts minimum)
    {
        ArgumentNullException.ThrowIfNull(minimum);

        return MeetsMinimum(minimum.Major, minimum.Minor, minimum.Build);
    }
}
