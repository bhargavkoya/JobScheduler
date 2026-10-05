using System.Security.Cryptography;
using System.Text;

namespace JobScheduler.Application.Runs;

/// <summary>Webhook secrets: random token shown once, only its SHA-256 is stored, compared in constant time.</summary>
public static class WebhookTokens
{
    public static string Generate() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static bool Verify(string? suppliedToken, string? storedHash)
    {
        if (string.IsNullOrEmpty(suppliedToken) || string.IsNullOrEmpty(storedHash)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(suppliedToken)), Encoding.ASCII.GetBytes(storedHash));
    }
}
