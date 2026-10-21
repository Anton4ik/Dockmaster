using System.Security.Cryptography;
using System.Text;

namespace Dockmaster.Server.Access;

/// <summary>
/// The code the first-run page asks for before it makes the first admin, printed to the server's log at start: whoever
/// reaches the UI port first is not enough, they also have to see the log. A new one at every start.
/// </summary>
public sealed class SetupCode
{
    // no 0/O, 1/I/L: read from a log and typed
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public string Value { get; } = string.Join("-", Enumerable.Range(0, 3).Select(_ => RandomNumberGenerator.GetString(Alphabet, 4)));

    /// <summary>Whether a typed code is this one (any case, dashes and spaces ignored).</summary>
    public bool Matches(string? typed)
    {
        static byte[] Norm(string s) => Encoding.ASCII.GetBytes(new string(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()));
        return typed != null && CryptographicOperations.FixedTimeEquals(Norm(typed), Norm(Value));
    }
}
