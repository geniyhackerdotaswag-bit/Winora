namespace Winora.System.Updates;

/// <summary>What became of the program when a swap was attempted.</summary>
public enum SwapResult
{
    /// <summary>The new program is in place.</summary>
    Replaced,

    /// <summary>The program is where it was and still runs. Either nothing moved, or what moved was put back.</summary>
    Unchanged,

    /// <summary>
    /// The program was moved aside and could not be put back or replaced.
    /// </summary>
    /// <remarks>
    /// The rarest outcome and the only one the caller cannot shrug off: the executable is no longer
    /// at its own path, and it is sitting beside it under <see cref="OldSuffix" />. Reported
    /// separately because telling somebody "nothing changed" while their program is missing is the
    /// one answer worse than saying nothing at all.
    /// </remarks>
    Displaced,
}

/// <summary>
/// Puts a downloaded program in the place of the running one.
/// </summary>
/// <remarks>
/// <para>
/// Windows refuses to delete or overwrite a running executable, but allows it to be renamed: the
/// loader opens the image with FILE_SHARE_DELETE, and a rename needs only that. This is why no
/// second program is needed to perform the update — a helper that waits for the first to exit is the
/// usual arrangement, and it is one more executable to ship, sign, and explain to an antivirus.
/// </para>
/// <para>
/// The order matters more than the mechanism. Up to the rename nothing has been destroyed, so any
/// failure leaves the program exactly as it was. The rename itself is reversible, because what it
/// produced is still the working program. Only after both have succeeded is there a moment where
/// the new file is in place, and by then there is nothing left to undo.
/// </para>
/// </remarks>
public static class AppFileSwap
{
    /// <summary>What the displaced program is renamed to.</summary>
    public const string OldSuffix = ".old";

    /// <summary>What a download in progress is called.</summary>
    public const string FreshSuffix = ".new";

    /// <summary>Replaces <paramref name="target" /> with <paramref name="fresh" />.</summary>
    public static SwapResult Replace(string target, string fresh) =>
        Replace(target, fresh, File.Move);

    /// <summary>The same, with the move operation supplied.</summary>
    /// <remarks>
    /// Exists for one test. The path where the rescue move fails leaves the program sitting beside
    /// its own name, and it is the only outcome a caller must act on — but it cannot be provoked
    /// through the file system, because the file the rescue needs is one the previous rename just
    /// created. Rather than leave that branch to inspection, the two moves are taken as a
    /// parameter, and the production entry point above passes the real one.
    /// </remarks>
    internal static SwapResult Replace(string target, string fresh, Action<string, string> move)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(fresh);

        if (!File.Exists(fresh) || !File.Exists(target))
        {
            return SwapResult.Unchanged;
        }

        var displaced = target + OldSuffix;

        try
        {
            // A leftover from a previous update would make the rename below fail. It is not needed:
            // whatever it holds has already been superseded once.
            TryDelete(displaced);

            move(target, displaced);
        }
        catch (Exception)
        {
            // Nothing has moved. The program is where it was and still runs.
            return SwapResult.Unchanged;
        }

        try
        {
            move(fresh, target);
            return SwapResult.Replaced;
        }
        catch (Exception)
        {
            // Put the working program back. If even this fails there is nothing further to try.
            try
            {
                move(displaced, target);
                return SwapResult.Unchanged;
            }
            catch (Exception)
            {
                // The program was moved aside and could not be restored. This is the only outcome
                // where the program is not where it belongs, so it must be reported distinctly.
                return SwapResult.Displaced;
            }
        }
    }

    /// <summary>
    /// Clears away what previous updates left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called at startup, when the displaced program is no longer running and can finally be
    /// deleted. Failure is silent on purpose: a file still held open is removed the next time, and a
    /// program that refused to start over a stale file would be worse than the stale file.
    /// </para>
    /// <para>
    /// <c>*.old</c> and <c>*.new</c> are debris only when <paramref name="executableName" /> is
    /// actually sitting beside them. After <see cref="SwapResult.Displaced" />, it is not: the
    /// executable itself is gone, <c>Winora.exe.new</c> is the one verified copy that can put the
    /// machine right, and <c>Winora.exe.old</c> is what the failure message tells the person to
    /// rename back by hand. Sweeping this folder without checking would delete both on the very next
    /// launch of any Winora — including a fresh copy run from Downloads, which clears this same
    /// folder on its own startup regardless of where it is itself running from.
    /// </para>
    /// </remarks>
    public static void RemoveLeftovers(string directory, string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            // The program this cleanup runs on behalf of has to still be there. Its absence means a
            // swap left the folder in the one state where .old and .new are not leftovers but the
            // only way back — see the remarks above.
            if (!File.Exists(Path.Combine(directory, executableName)))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*" + OldSuffix)
                         .Concat(Directory.EnumerateFiles(directory, "*" + FreshSuffix)))
            {
                TryDelete(file);
            }
        }
        catch (Exception)
        {
            // Same reasoning: never a reason to fail a startup.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Held open by something. Next time.
        }
    }
}
