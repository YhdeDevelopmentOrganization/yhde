using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace YHDE.Server.Accounts;

// Password hashing (ADR 0015): Argon2id with OWASP's parameters (19 MiB,
// 2 passes, 1 lane), a random 16-byte salt per password, stored as
//   $argon2id$v=19$m=19456,t=2,p=1$<salt b64>$<hash b64>
// Hashing uses real memory, so at most four run at a time.
public static class Passwords
{
    public const int MinLength = 10;
    public const int MaxLength = 128;
    private const int MemoryKib = 19456;
    private const int Passes = 2;
    private const int Lanes = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private static readonly SemaphoreSlim Gate = new(4, 4);

    // A hash nobody's password matches, to spend the same time on unknown
    // accounts as on real ones (no timing hint about which emails exist).
    private static readonly Lazy<string> Decoy = new(() => HashAsync(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))).GetAwaiter().GetResult());

    // Why a new password is refused, or null.
    public static string? Problem(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return $"Use at least {MinLength} characters.";
        if (password.Length > MaxLength) return $"Use at most {MaxLength} characters.";
        if (password.Distinct().Count() < 4) return "That password is too easy to guess.";
        var lower = password.ToLowerInvariant();
        if (email is not null && (lower == email.ToLowerInvariant() || lower == email.Split('@')[0].ToLowerInvariant()))
            return "Don't use your email address as your password.";
        if (Common.Contains(lower)) return "That password is too common. Try a short sentence instead.";
        return null;
    }

    public static async Task<string> HashAsync(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = await DeriveAsync(password, salt);
        return $"$argon2id$v=19$m={MemoryKib},t={Passes},p={Lanes}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static async Task<bool> VerifyAsync(string password, string? stored)
    {
        // An empty password is wrong, and costs the same time as any other.
        if (string.IsNullOrEmpty(password))
        {
            await DeriveAsync("\u0001", RandomNumberGenerator.GetBytes(SaltBytes));
            return false;
        }
        if (stored is null || !TryParse(stored, out var salt, out var expected))
        {
            await VerifyAsync(password, Decoy.Value);
            return false;
        }
        var actual = await DeriveAsync(password, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool TryParse(string stored, out byte[] salt, out byte[] hash)
    {
        salt = [];
        hash = [];
        var parts = stored.Split('$');
        // ["", "argon2id", "v=19", "m=19456,t=2,p=1", salt, hash]
        if (parts.Length != 6 || parts[1] != "argon2id" || parts[3] != $"m={MemoryKib},t={Passes},p={Lanes}") return false;
        try
        {
            salt = Convert.FromBase64String(parts[4]);
            hash = Convert.FromBase64String(parts[5]);
            return salt.Length == SaltBytes && hash.Length == HashBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<byte[]> DeriveAsync(string password, byte[] salt)
    {
        await Gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
                {
                    Salt = salt,
                    MemorySize = MemoryKib,
                    Iterations = Passes,
                    DegreeOfParallelism = Lanes,
                };
                return argon.GetBytes(HashBytes);
            });
        }
        finally
        {
            Gate.Release();
        }
    }

    // A few of the most used passwords of at least MinLength characters.
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "1234567890", "12345678910", "123456789a", "qwertyuiop", "password12", "password123", "password1234",
        "passwordpassword", "iloveyou12", "qwerty1234", "1q2w3e4r5t", "abcdefghij", "abcd123456", "asdfghjkl1",
        "0987654321", "1111111111", "1234qwerty", "qwertyuiop1", "letmein123", "welcome123", "admin12345",
        "administrator", "football123", "baseball123", "sunshine123", "princess123", "dragon1234", "monkey1234",
        "trustno1234", "superman123", "starwars123", "whatever123", "godotengine", "godot12345", "yhde123456",
    };
}

// Random secrets and their stored form.
public static class Secrets
{
    // 256 random bits, URL-safe.
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // PKCE (RFC 7636): the challenge sent to the provider for a verifier.
    public static string PkceChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}
