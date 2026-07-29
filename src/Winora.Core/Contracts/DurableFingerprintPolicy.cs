using Winora.Core.Changes;

namespace Winora.Core.Contracts;

internal static class DurableFingerprintPolicy
{
    internal const string Sha256Algorithm = "SHA-256";

    internal static void Validate(StateFingerprint fingerprint, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(fingerprint, parameterName);
        if (!StringComparer.Ordinal.Equals(fingerprint.Algorithm, Sha256Algorithm) ||
            fingerprint.Value is null ||
            fingerprint.Value.Length != 64 ||
            fingerprint.Value.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'A' and <= 'F')))
        {
            throw new ArgumentException(
                "A durable fingerprint must use canonical SHA-256 and a 64-character uppercase hexadecimal digest.",
                parameterName);
        }
    }
}
