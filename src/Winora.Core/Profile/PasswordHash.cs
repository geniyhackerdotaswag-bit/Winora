using System.Security.Cryptography;
using System.Text;

namespace Winora.Core.Profile;

/// <param name="Hash">The derived key, base64.</param>
/// <param name="Salt">The salt it was derived with, base64. Fresh for every profile.</param>
/// <param name="Iterations">How many rounds produced it, so a stored digest stays checkable.</param>
public sealed record PasswordDigest(string Hash, string Salt, int Iterations);

/// <summary>
/// Turns a password into something that can be checked but not read back.
/// </summary>
/// <remarks>
/// <para>
/// PBKDF2 out of the framework, deliberately not anything hand-written. The iteration count is
/// stored beside the digest rather than assumed, so it can be raised later without making every
/// existing profile unreadable — the old ones keep verifying at the count they were made with.
/// </para>
/// <para>
/// Worth being plain about what this does not do. Winora has no server: the digest sits in a file
/// beside the program, on the same machine as the person typing the password. It stops a password
/// being read out of that file; it does not stop anyone who has the file from using the program.
/// The registration screen says so in as many words.
/// </para>
/// </remarks>
public static class PasswordHash
{
    /// <summary>
    /// Rounds for a new password. Stored per digest, so this may rise over time.
    /// </summary>
    /// <remarks>
    /// OWASP's current minimum for PBKDF2-HMAC-SHA256. The count is stored per digest so
    /// existing profiles keep verifying at whatever they were made with.
    /// </remarks>
    public const int DefaultIterations = 600_000;

    /// <summary>
    /// The most rounds a stored digest may ask for.
    /// </summary>
    /// <remarks>
    /// Generous next to <see cref="DefaultIterations" />, so raising the default later needs no
    /// change here — but finite, because the count arrives from a file a person can edit and
    /// PBKDF2 does not refuse an absurd one. It simply runs, and a launch that hangs for an hour is
    /// worse than one that reports a problem: nothing is caught, nothing is shown, and there is
    /// nothing on screen to explain it.
    /// </remarks>
    public const int MaxIterations = 5_000_000;

    private const int SaltBytes = 16;

    private const int KeyBytes = 32;

    /// <summary>Turns a password into a digest that can be checked but not read back.</summary>
    /// <remarks>
    /// This method throws on a null password; <see cref="Verify" /> refuses a null password instead.
    /// <see cref="Create" /> is only ever reached from validated input, so the distinction is fine.
    /// </remarks>
    public static PasswordDigest Create(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Derive(password, salt, DefaultIterations);

        return new PasswordDigest(
            Convert.ToBase64String(key),
            Convert.ToBase64String(salt),
            DefaultIterations);
    }

    /// <summary>Whether this password produces the stored digest.</summary>
    /// <remarks>
    /// Every unusable digest answers false rather than throwing. The file it came from is one a
    /// person can open and edit, and this runs on the path that decides whether to show a window —
    /// an exception here would take the launch down over a bad character in a text file.
    /// </remarks>
    public static bool Verify(string password, PasswordDigest? digest)
    {
        if (password is null || digest is null || digest.Iterations <= 0 || digest.Iterations > MaxIterations)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(digest.Salt);
            var expected = Convert.FromBase64String(digest.Hash);

            if (salt.Length == 0 || expected.Length == 0)
            {
                return false;
            }

            var actual = Derive(password, salt, digest.Iterations);

            // Fixed-time comparison: the framework's own, so a difference in where two digests
            // diverge cannot be measured from outside.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception)
        {
            // Not base64, or a length the framework refuses. Either way: not this password.
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
}
