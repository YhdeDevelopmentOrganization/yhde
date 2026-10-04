using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using YHDE.Server.Domain;
using YHDE.Server.Persistence;

namespace YHDE.Server.Site;

public sealed record SitePost(
    Guid Id, string Kind, string Title, string Version, string Summary, string Body, string Cover,
    string Status, DateTimeOffset? PublishAt, DateTimeOffset Created, DateTimeOffset Updated);

public sealed record SiteMedia(string Hash, string ContentType, long Size, string Name, DateTimeOffset Created)
{
    public string Url => "/media/" + Hash;
}

public sealed record Announcement(bool Enabled, string Text, string Link, string Tone);
public sealed record Maintenance(bool Enabled, string Message);
public sealed record SiteSettings(Announcement Announcement, Maintenance Maintenance);
public sealed record EarlyAccessEntry(string Email, string Source, DateTimeOffset Created);

// The public website's content (admin.md "Site"): news posts and release
// notes with drafts and scheduling, the announcement bar, maintenance mode
// and the early-access list. Settings are cached in memory (every page
// request reads them); the admin page writes through here, so the cache is
// refreshed on every change. Every change is also written to audit_log.
public sealed partial class SiteStore(Database db, ILogger<SiteStore> logger)
{
    public const int MaxTitle = 160;
    public const int MaxBody = 40_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly SiteSettings Defaults = new(new Announcement(false, "", "", "info"), new Maintenance(false, ""));

    private SiteSettings _settings = Defaults;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _loading = new(1, 1);

    // Posts

    // Live posts, newest first: published, and their time has come.
    public async Task<IReadOnlyList<SitePost>> LiveAsync(string? kind, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<PostRow>(new CommandDefinition(
            """
            SELECT post_id, kind, title, version, summary, body, cover, status, publish_at, created_at, updated_at
            FROM site_posts
            WHERE deleted_at IS NULL AND status = 'published' AND (publish_at IS NULL OR publish_at <= NOW())
              AND (@kind::text IS NULL OR kind = @kind)
            ORDER BY COALESCE(publish_at, created_at) DESC
            LIMIT 200
            """,
            new { kind }, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<SitePost>> AllAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<PostRow>(new CommandDefinition(
            """
            SELECT post_id, kind, title, version, summary, body, cover, status, publish_at, created_at, updated_at
            FROM site_posts WHERE deleted_at IS NULL
            ORDER BY COALESCE(publish_at, updated_at) DESC
            """,
            cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public sealed record PostInput(string? Kind, string? Title, string? Version, string? Summary, string? Body, string? Cover, string? Status, DateTimeOffset? PublishAt);

    // Checks a post from the admin page; an error message, or null.
    public static string? Check(PostInput p)
    {
        if (p.Kind is not ("news" or "release")) return "Choose News or Release notes.";
        if (string.IsNullOrWhiteSpace(p.Title)) return "Give the post a title.";
        if (p.Title!.Length > MaxTitle) return $"Keep the title under {MaxTitle} characters.";
        if ((p.Body?.Length ?? 0) > MaxBody) return "The text is too long.";
        if (p.Status is not ("draft" or "published")) return "Save it as a draft or publish it.";
        if (p.Kind == "release" && string.IsNullOrWhiteSpace(p.Version)) return "Release notes need a version, like 0.4.0.";
        if (!string.IsNullOrEmpty(p.Cover) && !IsMediaUrl(p.Cover)) return "The cover must be a picture uploaded here.";
        return null;
    }

    public async Task<SitePost> SaveAsync(Guid? id, PostInput p, CancellationToken ct)
    {
        var postId = id ?? Guid.NewGuid();
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var args = new
        {
            id = postId,
            kind = p.Kind,
            title = p.Title!.Trim(),
            version = (p.Version ?? "").Trim(),
            summary = (p.Summary ?? "").Trim(),
            body = p.Body ?? "",
            cover = p.Cover ?? "",
            status = p.Status,
            publishAt = p.PublishAt?.UtcDateTime,
        };
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            id is null
                ? """
                  INSERT INTO site_posts (post_id, kind, title, version, summary, body, cover, status, publish_at)
                  VALUES (@id, @kind, @title, @version, @summary, @body, @cover, @status,
                          CASE WHEN @status = 'published' THEN COALESCE(@publishAt::timestamptz, NOW()) ELSE @publishAt::timestamptz END)
                  """
                : """
                  UPDATE site_posts SET kind = @kind, title = @title, version = @version, summary = @summary, body = @body, cover = @cover,
                         status = @status, updated_at = NOW(),
                         publish_at = CASE WHEN @status = 'published' THEN COALESCE(@publishAt::timestamptz, NOW()) ELSE @publishAt::timestamptz END
                  WHERE post_id = @id AND deleted_at IS NULL
                  """,
            args, tx, cancellationToken: ct));
        if (changed == 0) throw new KeyNotFoundException("That post is gone.");
        await AuditAsync(conn, tx, id is null ? "site.post_created" : "site.post_updated", postId,
            new { p.Kind, title = args.title, p.Status, publishAt = p.PublishAt }, ct);
        await tx.CommitAsync(ct);
        return (await AllAsync(ct)).First(x => x.Id == postId);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var title = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "UPDATE site_posts SET deleted_at = NOW() WHERE post_id = @id AND deleted_at IS NULL RETURNING title",
            new { id }, tx, cancellationToken: ct));
        if (title is null) return false;
        await AuditAsync(conn, tx, "site.post_deleted", id, new { title }, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // Settings (announcement bar, maintenance mode)

    // Cached; read on every page request.
    public async Task<SiteSettings> SettingsAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromSeconds(15)) return _settings;
        await _loading.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromSeconds(15)) return _settings;
            await using var conn = await db.OpenAsync(ct);
            var rows = (await conn.QueryAsync<(string key, string value)>(new CommandDefinition(
                "SELECT key, value::text FROM site_settings WHERE key IN ('announcement', 'maintenance')",
                cancellationToken: ct))).ToDictionary(r => r.key, r => r.value);
            _settings = new SiteSettings(
                Read(rows, "announcement", Defaults.Announcement),
                Read(rows, "maintenance", Defaults.Maintenance));
            _loadedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never take the site down over its own settings.
            logger.LogWarning(ex, "Site settings not loaded; using the last known ones");
            _loadedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _loading.Release();
        }
        return _settings;
    }

    private T Read<T>(Dictionary<string, string> rows, string key, T fallback)
    {
        if (!rows.TryGetValue(key, out var json)) return fallback;
        try { return JsonSerializer.Deserialize<T>(json, Json) ?? fallback; }
        catch (JsonException) { return fallback; }
    }

    public static string? Check(SiteSettings s)
    {
        if (s.Announcement.Enabled && string.IsNullOrWhiteSpace(s.Announcement.Text)) return "Write the announcement text, or turn it off.";
        if (s.Announcement.Text.Length > 200) return "Keep the announcement under 200 characters.";
        if (s.Announcement.Link.Length > 0 && !(s.Announcement.Link.StartsWith('/') || s.Announcement.Link.StartsWith("https://")))
            return "The link must start with / (a page on this site) or https://.";
        if (s.Announcement.Tone is not ("info" or "warning")) return "Choose a style for the announcement.";
        if (s.Maintenance.Message.Length > 400) return "Keep the maintenance message under 400 characters.";
        return null;
    }

    public async Task SaveSettingsAsync(SiteSettings s, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var (key, value) in new (string, object)[] { ("announcement", s.Announcement), ("maintenance", s.Maintenance) })
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO site_settings (key, value, updated_at) VALUES (@key, @value::jsonb, NOW())
                ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = NOW()
                """,
                new { key, value = JsonSerializer.Serialize(value, Json) }, tx, cancellationToken: ct));
        }
        await AuditAsync(conn, tx, "site.settings_changed", null, s, ct);
        await tx.CommitAsync(ct);
        _settings = s;
        _loadedAt = DateTimeOffset.UtcNow;
    }

    // Early access list

    [GeneratedRegex(@"^[^\s@<>""]{1,64}@[^\s@<>""]{1,190}\.[^\s@<>""]{2,24}$")]
    private static partial Regex EmailPattern();

    public static string? NormalizeEmail(string? email)
    {
        var e = (email ?? "").Trim().ToLowerInvariant();
        return e.Length <= 254 && EmailPattern().IsMatch(e) ? e : null;
    }

    // True when the address is new.
    public async Task<bool> AddEarlyAccessAsync(string email, string source, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var added = await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO early_access (email, source) VALUES (@email, @source) ON CONFLICT (email) DO NOTHING",
            new { email, source = source.Length > 40 ? source[..40] : source }, cancellationToken: ct));
        return added > 0;
    }

    public async Task<IReadOnlyList<EarlyAccessEntry>> EarlyAccessAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string email, string source, DateTime created_at)>(new CommandDefinition(
            "SELECT email, source, created_at FROM early_access ORDER BY created_at DESC", cancellationToken: ct));
        return rows.Select(r => new EarlyAccessEntry(r.email, r.source, Utc(r.created_at))).ToList();
    }

    // Removing someone (they asked, GDPR) leaves only an audit record without the address.
    public async Task<bool> RemoveEarlyAccessAsync(string email, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var removed = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM early_access WHERE email = @email", new { email }, tx, cancellationToken: ct));
        if (removed > 0) await AuditAsync(conn, tx, "site.early_access_removed", null, new { count = removed }, ct);
        await tx.CommitAsync(ct);
        return removed > 0;
    }

    // Pictures

    public const long MaxMediaBytes = 8L * 1024 * 1024;

    public static bool IsMediaUrl(string url) =>
        url.StartsWith("/media/", StringComparison.Ordinal) && Assets.BlobStore.IsValidHash(url["/media/".Length..]);

    // What an upload is, from its first bytes (never from its name or the
    // browser's claim). SVG is refused: it can carry script.
    public static string? SniffImage(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "image/jpeg";
        if (head.Length >= 6 && (head[..6].SequenceEqual("GIF87a"u8) || head[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    public async Task<SiteMedia> AddMediaAsync(string hash, string contentType, long size, string name, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO site_media (hash, content_type, size, name) VALUES (@hash, @contentType, @size, @name)
            ON CONFLICT (hash) DO NOTHING
            """,
            new { hash, contentType, size, name = name.Length > 200 ? name[..200] : name }, cancellationToken: ct));
        return (await MediaAsync(hash, ct))!;
    }

    public async Task<SiteMedia?> MediaAsync(string hash, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<MediaRow>(new CommandDefinition(
            "SELECT hash, content_type, size, name, created_at FROM site_media WHERE hash = @hash", new { hash }, cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<SiteMedia>> AllMediaAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<MediaRow>(new CommandDefinition(
            "SELECT hash, content_type, size, name, created_at FROM site_media ORDER BY created_at DESC LIMIT 200", cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    // Helpers

    private static Task AuditAsync(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, string type, Guid? target, object detail, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO audit_log (event_type, actor_id, target_id, detail) VALUES (@type, @actor, @target, @detail::jsonb)",
            new { type, actor = DevIdentity.ActorId, target, detail = JsonSerializer.Serialize(detail, Json) }, tx, cancellationToken: ct));

    private static DateTimeOffset Utc(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc));

#pragma warning disable IDE1006 // column names
    private sealed class PostRow
    {
        public Guid post_id { get; init; }
        public string kind { get; init; } = "";
        public string title { get; init; } = "";
        public string version { get; init; } = "";
        public string summary { get; init; } = "";
        public string body { get; init; } = "";
        public string cover { get; init; } = "";
        public string status { get; init; } = "";
        public DateTime? publish_at { get; init; }
        public DateTime created_at { get; init; }
        public DateTime updated_at { get; init; }

        public SitePost ToModel() => new(post_id, kind, title, version, summary, body, cover, status,
            publish_at is { } p ? Utc(p) : null, Utc(created_at), Utc(updated_at));
    }
    private sealed class MediaRow
    {
        public string hash { get; init; } = "";
        public string content_type { get; init; } = "";
        public long size { get; init; }
        public string name { get; init; } = "";
        public DateTime created_at { get; init; }

        public SiteMedia ToModel() => new(hash, content_type, size, name, Utc(created_at));
    }
#pragma warning restore IDE1006
}
