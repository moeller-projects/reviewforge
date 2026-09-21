using System.Security.Cryptography;
using System.Text;

namespace ReviewForge.Service.Security;

/// <summary>Constant-time API-key comparison over SHA-256 digests (fixed-length, no prefix oracle).</summary>
public static class ApiKeyValidator
{
    public static bool IsValid(string? presentedKey, IReadOnlyList<string> configuredKeys)
    {
        if (string.IsNullOrEmpty(presentedKey) || configuredKeys.Count == 0)
        {
            return false;
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));
        var match = false;
        foreach (var key in configuredKeys) // no early exit: do not leak which key matched
        {
            var expected = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            match |= CryptographicOperations.FixedTimeEquals(presented, expected);
        }

        return match;
    }
}
