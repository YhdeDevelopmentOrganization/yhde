using System.Security.Cryptography;
using System.Text;

namespace YHDE.Server.Teams;

// A project's join code: 8 characters (40 bits) in two groups, K7QM-2XRD.
// Short enough to read out or type; guessing is stopped by the per-account
// and per-address try limits on POST /api/team/join.
public static class JoinCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(9);
        for (var i = 0; i < 8; i++)
        {
            if (i == 4) sb.Append('-');
            sb.Append(Alphabet[bytes[i] & 31]);
        }
        return sb.ToString();
    }

    // Accepts lower case, spaces and missing dashes, and the letters people
    // mix up with digits (O for 0, I and L for 1). Null if it can't be one.
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new StringBuilder(8);
        foreach (var c in raw.ToUpperInvariant())
        {
            if (c is ' ' or '-' or '\t') continue;
            var d = c switch { 'O' => '0', 'I' or 'L' => '1', 'U' => 'V', _ => c };
            if (Alphabet.IndexOf(d) < 0 || sb.Length == 8) return null;
            sb.Append(d);
        }
        return sb.Length == 8 ? $"{sb.ToString(0, 4)}-{sb.ToString(4, 4)}" : null;
    }
}
