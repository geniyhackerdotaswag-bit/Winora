using System.Security.Cryptography;

namespace Winora.System.Updates;

/// <summary>What was decided about a downloaded file.</summary>
public enum DownloadVerdict
{
    /// <summary>Safe to put in place.</summary>
    Ok,

    /// <summary>Not the length the release said. A dropped connection, or a full disk.</summary>
    WrongSize,

    /// <summary>The right length, the wrong bytes.</summary>
    WrongHash,

    /// <summary>Not a Windows program at all — most often a web page.</summary>
    NotAnExecutable,

    /// <summary>Could not be read to judge.</summary>
    Unreadable,
}

/// <summary>
/// Decides whether a downloaded file may replace the running program.
/// </summary>
/// <remarks>
/// <para>
/// What this protects against is a broken download, not a forged release. The checksum is published
/// by the same workflow run, in the same release, and served from the same host as the program: it
/// cannot vouch for the program's origin, only for its arrival. The origin is vouched for by HTTPS
/// to GitHub, and calling the checksum a defence against tampering would be a claim this code does
/// not support.
/// </para>
/// <para>
/// The order is deliberate: size first because it costs nothing, then the two bytes at the front,
/// then the hash, which is the only one that reads the whole file.
/// </para>
/// </remarks>
public static class AppDownloadCheck
{
    /// <summary>The first two bytes of every Windows executable.</summary>
    private static ReadOnlySpan<byte> ExecutableSignature => "MZ"u8;

    /// <param name="path">The downloaded file.</param>
    /// <param name="expectedSize">The length the release said it would be.</param>
    /// <param name="expectedSha256">
    /// The contents of the checksum file. May be bare hex, or hex followed by a file name the way
    /// <c>sha256sum</c> writes it.
    /// </param>
    public static DownloadVerdict Verify(string path, long expectedSize, string? expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return DownloadVerdict.Unreadable;
            }

            if (file.Length != expectedSize)
            {
                return DownloadVerdict.WrongSize;
            }

            if (!StartsLikeAProgram(path))
            {
                return DownloadVerdict.NotAnExecutable;
            }

            return HashMatches(path, expectedSha256)
                ? DownloadVerdict.Ok
                : DownloadVerdict.WrongHash;
        }
        catch (Exception)
        {
            // Locked, gone, or on a disk that stopped answering. Not a reason to throw out of a
            // download, and certainly not a reason to install it.
            return DownloadVerdict.Unreadable;
        }
    }

    private static bool StartsLikeAProgram(string path)
    {
        using var stream = File.OpenRead(path);

        Span<byte> head = stackalloc byte[2];
        return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length &&
               head.SequenceEqual(ExecutableSignature);
    }

    private static bool HashMatches(string path, string? expected)
    {
        // sha256sum writes "<hex>  <name>". Only the first word is the hash.
        var wanted = expected?.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (wanted is not { Length: 64 })
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));

        return string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase);
    }
}
