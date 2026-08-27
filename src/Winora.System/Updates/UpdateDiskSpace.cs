namespace Winora.System.Updates;

/// <summary>What an update needs on disk, and whether it is there.</summary>
/// <param name="Needed">Bytes the whole update needs.</param>
/// <param name="Free">Bytes actually free where it is needed.</param>
public readonly record struct UpdateSpace(long Needed, long Free)
{
    public bool Fits => Free >= Needed;

    /// <summary>How much more is wanted, or zero when it fits.</summary>
    public long Short => Fits ? 0 : Needed - Free;
}

/// <summary>
/// Whether an update will fit, asked before anything is downloaded.
/// </summary>
/// <remarks>
/// <para>
/// A single-file build is not finished arriving when the download is. Before it can start, .NET
/// unpacks it into a folder under the temporary directory, and that unpacked copy is larger than
/// the file it came from: measured on 2026-08-27, an 88 MB Winora produced 206 MB of extracted
/// contents — 2.3 times over.
/// </para>
/// <para>
/// Getting this wrong is the worst failure this program has. The download succeeds, the swap
/// succeeds, Winora restarts, and the new copy dies before a single line of its own code runs, so
/// there is nothing left that could put a message on screen. From the outside the program simply
/// stops opening. That is what happened on the owner's machine an hour before this was written,
/// and the only trace was one line in the Windows event log.
/// </para>
/// </remarks>
public static class UpdateDiskSpace
{
    /// <summary>
    /// How much bigger the unpacked copy is than the file.
    /// </summary>
    /// <remarks>
    /// Measured, not assumed: 206 MB unpacked from 88 MB, which is 2.34. Rounded up, because being
    /// generous here costs a person nothing and being tight costs them a program that will not open.
    /// </remarks>
    private const double ExtractedMultiple = 2.5;

    /// <summary>Room left over so the machine is not driven to the last byte.</summary>
    private const long Margin = 256L * 1024 * 1024;

    /// <summary>
    /// What an update of this size needs in total: the download, the unpacked copy, and a margin.
    /// </summary>
    public static long NeededFor(long downloadBytes) =>
        downloadBytes + (long)(downloadBytes * ExtractedMultiple) + Margin;

    /// <summary>
    /// Measures the room against what the update needs.
    /// </summary>
    /// <param name="downloadBytes">The size of the release file.</param>
    /// <param name="freeBytes">Bytes free where the program and its unpacked copy will live.</param>
    public static UpdateSpace For(long downloadBytes, long freeBytes) =>
        new(NeededFor(downloadBytes), freeBytes);
}
