using System.IO.Compression;

namespace Winora.System.Windows;

/// <summary>Unpacks pack files out of archives the user dropped in a pack folder.</summary>
public interface IArchiveExtractor
{
    /// <summary>
    /// Extracts cursor files from every archive under <paramref name="rootDirectory"/> that has not
    /// been extracted yet. Returns how many archives were unpacked.
    /// </summary>
    int ExtractPending(string rootDirectory);
}

/// <summary>
/// Unpacks a chosen set of file types out of <c>.zip</c> archives, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Cursor packs are distributed as archives, so a folder of untouched <c>.zip</c> files finds
/// nothing without this. Unpacking is reading, not executing — but a zip is still a file from a
/// stranger, and this app runs elevated, so three guards are non-negotiable and each has a test:
/// </para>
/// <list type="number">
/// <item>
/// Only entries with an allowed extension are written. Everything else is skipped, including
/// <c>install.inf</c>: that file is what makes a downloaded pack dangerous, because installing it
/// hands Windows a list of commands somebody else wrote.
/// </item>
/// <item>
/// Every entry's destination is resolved and checked to be inside the target folder. An archive
/// entry named <c>..\..\Windows\System32\x.cur</c> is a real attack, not a hypothetical one, and
/// this is the check that stops it.
/// </item>
/// <item>
/// Extraction stops at a total size cap, so an archive that expands to far more than it claims
/// cannot fill the disk.
/// </item>
/// </list>
/// <para>
/// Already-extracted archives are left alone, so this is safe to run on every visit to the screen.
/// </para>
/// </remarks>
public sealed class ArchiveExtractor : IArchiveExtractor
{
    /// <summary>Generous for cursors, which are kilobytes, and far short of filling a disk.</summary>
    private const long MaxExtractedBytes = 64L * 1024 * 1024;

    private readonly string[] _extensions;

    /// <param name="extensions">
    /// The only extensions written to disk. Everything else in the archive is skipped, which is how
    /// an installer script never reaches the file system.
    /// </param>
    public ArchiveExtractor(params string[] extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        if (extensions.Length == 0)
        {
            throw new ArgumentException("An extractor with no allowed extensions would write nothing.", nameof(extensions));
        }

        _extensions = extensions;
    }

    public int ExtractPending(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Directory.Exists(rootDirectory))
        {
            return 0;
        }

        var extracted = 0;
        IEnumerable<string> archives;
        try
        {
            archives = Directory.EnumerateFiles(rootDirectory, "*.zip", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return 0;
        }

        foreach (var archive in archives)
        {
            var destination = Path.Combine(
                Path.GetDirectoryName(archive)!,
                Path.GetFileNameWithoutExtension(archive));

            // Already unpacked: leave it, so a visit to the screen never redoes work or overwrites
            // something the user has since changed.
            if (Directory.Exists(destination))
            {
                continue;
            }

            if (TryExtract(archive, destination, _extensions))
            {
                extracted++;
            }
        }

        return extracted;
    }

    private static bool TryExtract(string archivePath, string destination, string[] extensions)
    {
        var wrote = false;
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var full = Path.GetFullPath(destination);
            var budget = MaxExtractedBytes;

            foreach (var entry in archive.Entries)
            {
                if (entry.Name.Length == 0 ||
                    !extensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.Length > budget)
                {
                    break;
                }

                // Flattened deliberately: the entry's own directory path is discarded and only its
                // file name is used, which removes traversal as a possibility rather than merely
                // detecting it. The containment check below then still verifies the result.
                var target = Path.GetFullPath(Path.Combine(full, Path.GetFileName(entry.Name)));
                if (!target.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    continue;
                }

                Directory.CreateDirectory(full);
                entry.ExtractToFile(target, overwrite: false);
                budget -= entry.Length;
                wrote = true;
            }
        }
        catch (Exception)
        {
            // A corrupt or unreadable archive yields nothing. The screen then simply shows no pack
            // for it, which is honest and costs the user only the archive they chose to add.
            return wrote;
        }

        return wrote;
    }
}
