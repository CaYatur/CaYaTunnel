using System.Security.Cryptography;
using System.Text;

namespace CaYaTunnel.Core.Security;

/// <summary>
/// The shared secret a provisioned client presents on every connect.
/// <para>
/// It is deliberately *not* a bearer token the client validates locally — the server checks it
/// on each handshake, so rotating the key on the server immediately disables every client build
/// that still carries the old one, which is exactly the kill switch the operator needs.
/// </para>
/// </summary>
public static class EnrollmentKey
{
    private const int KeyBytes = 32; // 256 bits of entropy

    /// <summary>Generates a fresh key, formatted in readable groups.</summary>
    public static string Generate()
        => Base32.Group(Base32.Encode(RandomNumberGenerator.GetBytes(KeyBytes)));

    /// <summary>
    /// Hash used for storage. The key is 256 bits of machine-generated randomness, so it is not
    /// guessable and needs no password-stretching KDF; a salted SHA-256 keeps the plaintext off
    /// disk and out of memory dumps without adding per-connect cost.
    /// </summary>
    public static string Hash(string key, string salt)
    {
        var normalised = Base32.Normalise(key);
        var bytes = Encoding.UTF8.GetBytes(salt + ':' + normalised);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string NewSalt() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Constant-time comparison, so a wrong key leaks nothing through timing.</summary>
    public static bool Verify(string presentedKey, string expectedHash, string salt)
    {
        if (string.IsNullOrEmpty(presentedKey) || string.IsNullOrEmpty(expectedHash))
        {
            return false;
        }

        var candidate = Encoding.UTF8.GetBytes(Hash(presentedKey, salt));
        var expected = Encoding.UTF8.GetBytes(expectedHash);
        return CryptographicOperations.FixedTimeEquals(candidate, expected);
    }

    /// <summary>
    /// Compares two keys in their normalised form, in constant time. Used when the server holds
    /// the current key in plaintext (it must, to embed it in provisioned builds) rather than as
    /// a hash.
    /// </summary>
    public static bool Matches(string? presented, string? expected)
    {
        if (string.IsNullOrWhiteSpace(presented) || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(Base32.Normalise(presented));
        var b = Encoding.UTF8.GetBytes(Base32.Normalise(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Short, non-reversible label for showing which key a build carries in the UI.</summary>
    public static string Fingerprint(string key)
    {
        var normalised = Base32.Normalise(key);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Base32.Encode(hash.AsSpan(0, 5));
    }
}
