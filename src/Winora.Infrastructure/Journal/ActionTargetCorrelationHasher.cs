using System.Security.Cryptography;
using System.Text;

namespace Winora.Infrastructure.Journal;

public sealed class ActionTargetCorrelationHasher
{
    public const int MinimumSaltSize = 32;

    private readonly byte[] _localSalt;

    public ActionTargetCorrelationHasher(ReadOnlySpan<byte> localSalt)
    {
        if (localSalt.Length < MinimumSaltSize)
        {
            throw new ArgumentException(
                $"The local correlation salt must contain at least {MinimumSaltSize} bytes.",
                nameof(localSalt));
        }

        _localSalt = localSalt.ToArray();
    }

    public string Hash(string stableTargetIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableTargetIdentifier);
        if (stableTargetIdentifier.Length > 4096 || stableTargetIdentifier.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The target identifier is too long or contains control characters.",
                nameof(stableTargetIdentifier));
        }

        var targetBytes = Encoding.UTF8.GetBytes(stableTargetIdentifier);
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(_localSalt, targetBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(targetBytes);
        }
    }

    public static byte[] CreateSalt() => RandomNumberGenerator.GetBytes(MinimumSaltSize);
}
