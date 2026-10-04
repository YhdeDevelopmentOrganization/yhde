using System.Text.Json;
using Dapper;
using Npgsql;
using YHDE.Server.Persistence;

namespace YHDE.Server.Accounts;

public sealed record User(Guid Id, string Email, bool EmailVerified, string Name, bool HasPassword, DateTimeOffset Created, bool Disabled);
public sealed record UserSession(Guid Id, Guid UserId, string Kind, string Device, string Ip, DateTimeOffset Created, DateTimeOffset LastSeen, DateTimeOffset Expires);
public sealed record LinkedProvider(string Provider, string Email, DateTimeOffset Created);

// Accounts in PostgreSQL (007_accounts.sql). Tokens (sessions, email links)
// are stored only as SHA-256 hashes; this class never sees a password, only
// its Argon2id hash.
public sealed class AccountStore(Database db)
{
    public static readonly TimeSpan WebSessionLifetime = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    // Users

    public async Task<User?> ByIdAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(UserRow.Select + " WHERE user_id = @id", new { id }, cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task<(User User, string? PasswordHash)?> ByEmailAsync(string email, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            UserRow.Select + " WHERE email = @email", new { email = NormalizeEmail(email) }, cancellationToken: ct));
        return row is null ? null : (row.ToModel(), row.password_hash);
    }

    public async Task<string?> PasswordHashAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT password_hash FROM users WHERE user_id = @userId", new { userId }, cancellationToken: ct));
    }

    // Null when the email is taken.
    public async Task<User?> CreateAsync(string email, string name, string? passwordHash, bool verified, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var conn = await db.OpenAsync(ct);
        var added = await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO users (user_id, email, display_name, password_hash, email_verified_at)
            VALUES (@id, @email, @name, @passwordHash, CASE WHEN @verified THEN NOW() ELSE NULL END)
            ON CONFLICT (email) DO NOTHING
            """,
            new { id, email = NormalizeEmail(email), name, passwordHash, verified }, cancellationToken: ct));
        if (added == 0) return null;
        await AuditAsync(conn, "account.created", id, new { method = passwordHash is null ? "provider" : "password" }, ct);
        return await ByIdAsync(id, ct);
    }

    public async Task SetPasswordAsync(Guid userId, string passwordHash, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET password_hash = @passwordHash WHERE user_id = @userId", new { userId, passwordHash }, cancellationToken: ct));
        await AuditAsync(conn, "account.password_changed", userId, new { }, ct);
    }

    public async Task SetNameAsync(Guid userId, string name, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET display_name = @name WHERE user_id = @userId", new { userId, name }, cancellationToken: ct));
    }

    public async Task MarkVerifiedAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET email_verified_at = COALESCE(email_verified_at, NOW()) WHERE user_id = @userId", new { userId }, cancellationToken: ct));
    }

    // Sessions

    // Returns the token to hand out; only its hash is stored.
    public async Task<string> CreateSessionAsync(Guid userId, string kind, string device, string ip, TimeSpan lifetime, CancellationToken ct)
    {
        var token = Secrets.NewToken();
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO user_sessions (session_id, user_id, kind, token_hash, device, ip, expires_at)
            VALUES (@id, @userId, @kind, @hash, @device, @ip, @expires)
            """,
            new
            {
                id = Guid.NewGuid(), userId, kind, hash = Secrets.Hash(token),
                device = Clip(device, 200), ip = Clip(ip, 64), expires = DateTime.UtcNow.Add(lifetime),
            }, cancellationToken: ct));
        await AuditAsync(conn, "account.signed_in", userId, new { kind, ip = Clip(ip, 64) }, ct);
        return token;
    }

    // The live session for a token, touching its last use (at most once a minute).
    public async Task<(UserSession Session, User User)?> SessionAsync(string token, string kind, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            """
            UPDATE user_sessions SET last_seen_at = NOW()
            WHERE token_hash = @hash AND kind = @kind AND revoked_at IS NULL AND expires_at > NOW()
              AND last_seen_at < NOW() - INTERVAL '1 minute'
            RETURNING session_id, user_id, kind, device, ip, created_at, last_seen_at, expires_at
            """,
            new { hash = Secrets.Hash(token), kind }, cancellationToken: ct))
            ?? await conn.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            """
            SELECT session_id, user_id, kind, device, ip, created_at, last_seen_at, expires_at FROM user_sessions
            WHERE token_hash = @hash AND kind = @kind AND revoked_at IS NULL AND expires_at > NOW()
            """,
            new { hash = Secrets.Hash(token), kind }, cancellationToken: ct));
        if (row is null) return null;
        var user = await ByIdAsync(row.user_id, ct);
        if (user is null || user.Disabled) return null;
        return (row.ToModel(), user);
    }

    public async Task<IReadOnlyList<UserSession>> SessionsAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<SessionRow>(new CommandDefinition(
            """
            SELECT session_id, user_id, kind, device, ip, created_at, last_seen_at, expires_at FROM user_sessions
            WHERE user_id = @userId AND revoked_at IS NULL AND expires_at > NOW()
            ORDER BY last_seen_at DESC
            """,
            new { userId }, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE user_sessions SET revoked_at = NOW() WHERE session_id = @sessionId AND user_id = @userId AND revoked_at IS NULL",
            new { userId, sessionId }, cancellationToken: ct));
        if (n > 0) await AuditAsync(conn, "account.session_ended", userId, new { sessionId }, ct);
        return n > 0;
    }

    public async Task RevokeTokenAsync(string token, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE user_sessions SET revoked_at = NOW() WHERE token_hash = @hash AND revoked_at IS NULL",
            new { hash = Secrets.Hash(token) }, cancellationToken: ct));
    }

    // After a password change or reset: every session but (optionally) one ends.
    public async Task RevokeOtherSessionsAsync(Guid userId, Guid? keep, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE user_sessions SET revoked_at = NOW() WHERE user_id = @userId AND revoked_at IS NULL AND (@keep::uuid IS NULL OR session_id <> @keep)",
            new { userId, keep }, cancellationToken: ct));
    }

    // One-time email links

    public async Task<string> CreateEmailTokenAsync(Guid userId, string purpose, TimeSpan lifetime, CancellationToken ct)
    {
        var token = Secrets.NewToken();
        await using var conn = await db.OpenAsync(ct);
        // A new link replaces older unused ones for the same purpose.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE email_tokens SET used_at = NOW() WHERE user_id = @userId AND purpose = @purpose AND used_at IS NULL",
            new { userId, purpose }, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO email_tokens (token_hash, user_id, purpose, expires_at) VALUES (@hash, @userId, @purpose, @expires)",
            new { hash = Secrets.Hash(token), userId, purpose, expires = DateTime.UtcNow.Add(lifetime) }, cancellationToken: ct));
        return token;
    }

    // Uses a link once; returns its user, or null when it is wrong, used or expired.
    public async Task<Guid?> UseEmailTokenAsync(string token, string purpose, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            """
            UPDATE email_tokens SET used_at = NOW()
            WHERE token_hash = @hash AND purpose = @purpose AND used_at IS NULL AND expires_at > NOW()
            RETURNING user_id
            """,
            new { hash = Secrets.Hash(token), purpose }, cancellationToken: ct));
    }

    // Linked providers

    public async Task<Guid?> UserForProviderAsync(string provider, string providerUserId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT user_id FROM oauth_links WHERE provider = @provider AND provider_user_id = @providerUserId",
            new { provider, providerUserId }, cancellationToken: ct));
    }

    public async Task LinkProviderAsync(Guid userId, string provider, string providerUserId, string email, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO oauth_links (provider, provider_user_id, user_id, email) VALUES (@provider, @providerUserId, @userId, @email)
            ON CONFLICT (provider, provider_user_id) DO NOTHING
            """,
            new { provider, providerUserId, userId, email = NormalizeEmail(email) }, cancellationToken: ct));
        await AuditAsync(conn, "account.provider_linked", userId, new { provider }, ct);
    }

    public async Task<IReadOnlyList<LinkedProvider>> ProvidersAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string provider, string email, DateTime created_at)>(new CommandDefinition(
            "SELECT provider, email, created_at FROM oauth_links WHERE user_id = @userId ORDER BY created_at", new { userId }, cancellationToken: ct));
        return rows.Select(r => new LinkedProvider(r.provider, r.email, Utc(r.created_at))).ToList();
    }

    public async Task<bool> UnlinkProviderAsync(Guid userId, string provider, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM oauth_links WHERE user_id = @userId AND provider = @provider", new { userId, provider }, cancellationToken: ct));
        if (n > 0) await AuditAsync(conn, "account.provider_unlinked", userId, new { provider }, ct);
        return n > 0;
    }

    // Helpers

    private static Task AuditAsync(NpgsqlConnection conn, string type, Guid userId, object detail, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO audit_log (event_type, actor_id, target_id, detail) VALUES (@type, @userId, @userId, @detail::jsonb)",
            new { type, userId, detail = JsonSerializer.Serialize(detail, Json) }, cancellationToken: ct));

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
    private static DateTimeOffset Utc(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc));

#pragma warning disable IDE1006 // column names
    private sealed class UserRow
    {
        public const string Select = "SELECT user_id, email, email_verified_at, display_name, password_hash, created_at, disabled_at FROM users";
        public Guid user_id { get; init; }
        public string email { get; init; } = "";
        public DateTime? email_verified_at { get; init; }
        public string display_name { get; init; } = "";
        public string? password_hash { get; init; }
        public DateTime created_at { get; init; }
        public DateTime? disabled_at { get; init; }

        public User ToModel() => new(user_id, email, email_verified_at is not null, display_name, password_hash is not null, Utc(created_at), disabled_at is not null);
    }

    private sealed class SessionRow
    {
        public Guid session_id { get; init; }
        public Guid user_id { get; init; }
        public string kind { get; init; } = "";
        public string device { get; init; } = "";
        public string ip { get; init; } = "";
        public DateTime created_at { get; init; }
        public DateTime last_seen_at { get; init; }
        public DateTime expires_at { get; init; }

        public UserSession ToModel() => new(session_id, user_id, kind, device, ip, Utc(created_at), Utc(last_seen_at), Utc(expires_at));
    }
#pragma warning restore IDE1006
}
