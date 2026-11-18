using System;
using System.IO;
using System.Security.Cryptography;

namespace Layal.Services;

/// <summary>
/// Validates full Ollama model digests and detects drift against a locally persisted pin
/// that is populated only after the installed digest matches the official Ollama Registry
/// manifest. The local pin is not a separate publisher signature.
/// </summary>
public static class OllamaModelDigestPolicy
{
    public static bool IsValidSha256Digest(string? digest)
    {
        if (digest is null || digest.Length != 64)
            return false;

        foreach (var character in digest)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }

        return true;
    }

    public static bool Matches(string expectedDigest, string actualDigest)
    {
        if (!IsValidSha256Digest(expectedDigest) || !IsValidSha256Digest(actualDigest))
            return false;

        var expected = Convert.FromHexString(expectedDigest);
        var actual = Convert.FromHexString(actualDigest);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static void EnsureMatches(string expectedDigest, string actualDigest)
    {
        if (!IsValidSha256Digest(expectedDigest))
            throw new InvalidDataException("The persisted model pin is not a full SHA-256 digest.");
        if (!IsValidSha256Digest(actualDigest))
            throw new InvalidDataException("The installed model did not report a full SHA-256 digest.");
        if (!Matches(expectedDigest, actualDigest))
            throw new InvalidDataException(
                "The installed model digest differs from the first-verified local pin. Silent model replacement is blocked.");
    }
}
