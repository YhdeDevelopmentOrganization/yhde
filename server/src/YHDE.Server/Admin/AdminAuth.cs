using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace YHDE.Server.Admin;

// Someone signed in on the admin page: a staff account (admin or support),
// or the server password (Yhde:AdminPassword), which acts as an admin.
public sealed record StaffSession(DateTimeOffset Until, Guid? UserId, string Name, string Email, string Role)
{
    public bool IsAdmin => Role == "admin";
}

// Who may use the admin page (admin.md):
//
// - Staff accounts: a YHDE account with the role admin or support
//   (users.staff_role), signing in with its email and password, or with the
//   website session it already has. Roles are given on the admin page.
// - The server password (Yhde:AdminPassword, at least 12 characters): the
//   way in before any account has a role, and in an emergency. Off if unset.
// - A sign-in gets a random session token in an HttpOnly, SameSite=Strict
//   cookie scoped to /admin; sessions end after 12 hours, and at once when
//   the person's role is taken away or their account is disabled.
// - Every change additionally needs the X-YHDE-Admin header, which a page on
//   another site cannot send (cross-site request forgery).
// - Five wrong server passwords from one address lock that address out for
//   15 minutes; account passwords have the website's sign-in limits.
public sealed class AdminAuth
{
    public const string CookieName = "yhde_admin";
    public const string HeaderName = "X-YHDE-Admin";
    public const int MinimumPasswordLength = 12;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    public const int MaxFailures = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    private readonly byte[]? _passwordHash;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, StaffSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Since)> _failures = new(StringComparer.Ordinal);

    public AdminAuth(IConfiguration configuration, TimeProvider time, ILogger<AdminAuth> logger)
    {
        _time = time;
        var password = configuration["Yhde:AdminPassword"]?.Trim();
        if (string.IsNullOrEmpty(password))
        {
            logger.LogInformation("The admin page's server password is off (set Yhde:AdminPassword to turn it on); staff accounts can still sign in");
            return;
        }
        if (password.Length < MinimumPasswordLength)
        {
            throw new InvalidOperationException(
                $"Yhde:AdminPassword must be at least {MinimumPasswordLength} characters.");
        }
        _passwordHash = Hash(password);
    }

    // Whether the server password works (staff accounts always can sign in).
    public bool PasswordEnabled => _passwordHash is not null;

    public enum LoginResult { Ok, Wrong, LockedOut, Disabled }

    public (LoginResult Result, string? Token) Login(string? password, IPAddress? from)
    {
        if (_passwordHash is null) return (LoginResult.Disabled, null);
        var now = _time.GetUtcNow();
        var who = from?.ToString() ?? "unknown";
        if (_failures.TryGetValue(who, out var f) && f.Count >= MaxFailures && now - f.Since < Lockout)
            return (LoginResult.LockedOut, null);

        if (string.IsNullOrEmpty(password) || !CryptographicOperations.FixedTimeEquals(Hash(password.Trim()), _passwordHash))
        {
            _failures.AddOrUpdate(who, _ => (1, now),
                (_, old) => now - old.Since >= Lockout ? (1, now) : (old.Count + 1, old.Since));
            return (LoginResult.Wrong, null);
        }
        _failures.TryRemove(who, out _);
        return (LoginResult.Ok, Start(null, "Server password", "", "admin"));
    }

    // A session for a staff account that has proved who it is.
    public string Start(Guid? userId, string name, string email, string role)
    {
        if (_sessions.Count > 10_000) Sweep();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _sessions[token] = new StaffSession(_time.GetUtcNow() + SessionLifetime, userId, name, email, role);
        return token;
    }

    public StaffSession? Get(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var s)) return null;
        if (s.Until > _time.GetUtcNow()) return s;
        _sessions.TryRemove(token, out _);
        return null;
    }

    public bool IsValid(string? token) => Get(token) is not null;

    public void Logout(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(token, out _);
    }

    // Ends every admin session of an account (role taken away, disabled).
    public void EndFor(Guid userId)
    {
        foreach (var (token, s) in _sessions)
            if (s.UserId == userId) _sessions.TryRemove(token, out _);
    }

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var (token, s) in _sessions)
            if (s.Until <= now) _sessions.TryRemove(token, out _);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
