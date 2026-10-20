using System.Security.Cryptography;

namespace Dockmaster.Server.Access;

/// <summary>PBKDF2 password hashes: "pbkdf2$salt$hash" (the format the admin password always had).</summary>
public static class Passwords
{
    public const int MinLength = 8;
    public static readonly string TooShort = $"a password has at least {MinLength} characters";

    private const int Iterations = 200_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    // compared against when the name is unknown: a wrong name takes as long as a wrong password
    private static readonly Lazy<string> Dummy = new(() => Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <param name="stored">null: no such user — the work is done all the same, and the answer is false</param>
    public static bool Verify(string password, string? stored)
    {
        var parts = (stored ?? Dummy.Value).Split('$');
        if (parts.Length != 3 || parts[0] != "pbkdf2") return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return CryptographicOperations.FixedTimeEquals(actual, expected) && stored != null;
    }

    /// <summary>A new session stamp: sessions signed in under another one are over.</summary>
    public static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
