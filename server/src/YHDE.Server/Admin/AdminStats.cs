using System.Runtime.InteropServices;
using Dapper;
using YHDE.Server.Assets;
using YHDE.Server.Gateway;
using YHDE.Server.Persistence;

namespace YHDE.Server.Admin;

// Everything the admin page charts (admin.md): activity, people, storage
// and the server's health. Built on request and kept for 30 seconds.
public sealed class AdminStats(Database db, BlobStore blobs, BlobStoreOptions blobOptions, HealthMonitor health, ISessionManager sessions)
{
    public const int Days = 30;
    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, object Value)? _cache;
    private (DateTimeOffset At, long Files, long Bytes) _usage;

    public void Invalidate() => _cache = null;

    public async Task<object> GetAsync(CancellationToken ct)
    {
        if (_cache is { } c && DateTimeOffset.UtcNow - c.At < CacheTime) return c.Value;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } c2 && DateTimeOffset.UtcNow - c2.At < CacheTime) return c2.Value;
            var value = await BuildAsync(ct);
            _cache = (DateTimeOffset.UtcNow, value);
            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<object> BuildAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var p = new { days = Days };
        CommandDefinition Q(string sql) => new(sql, p, commandTimeout: 120, cancellationToken: ct);

        // Activity
        var activity = await conn.QueryAsync<(DateTime day, Guid project_id, long n)>(Q(
            """
            SELECT date_trunc('day', o.created_at AT TIME ZONE 'UTC') AS day, b.project_id, COUNT(*) AS n
            FROM operations o JOIN branches b ON b.branch_id = o.branch_id
            WHERE o.created_at > NOW() - make_interval(days => @days)
            GROUP BY 1, 2 ORDER BY 1
            """));
        var heat = await conn.QueryAsync<(int dow, int hour, long n)>(Q(
            """
            SELECT EXTRACT(ISODOW FROM created_at AT TIME ZONE 'UTC')::int AS dow,
                   EXTRACT(HOUR FROM created_at AT TIME ZONE 'UTC')::int AS hour, COUNT(*) AS n
            FROM operations WHERE created_at > NOW() - make_interval(days => @days)
            GROUP BY 1, 2
            """));
        var kinds = await conn.QueryAsync<(string type, long n)>(Q(
            """
            SELECT type, COUNT(*) AS n FROM operations
            WHERE created_at > NOW() - make_interval(days => @days) GROUP BY 1 ORDER BY 2 DESC
            """));
        var totals = await conn.QuerySingleAsync<(long all_ops, long today, long week)>(Q(
            """
            SELECT COUNT(*) AS all_ops,
                   COUNT(*) FILTER (WHERE created_at > date_trunc('day', NOW())) AS today,
                   COUNT(*) FILTER (WHERE created_at > NOW() - INTERVAL '7 days') AS week
            FROM operations
            """));

        // People
        var people = (await conn.QueryAsync<PersonRow>(Q(
            $"""
            SELECT member_id,
                   (array_agg(member_name ORDER BY started_at DESC))[1] AS name,
                   COUNT(*) AS sessions,
                   COALESCE(SUM(EXTRACT(EPOCH FROM COALESCE(ended_at, last_seen_at) - started_at)), 0)::bigint AS seconds,
                   MAX(COALESCE(ended_at, last_seen_at)) AS last_seen,
                   MIN(started_at) AS first_seen,
                   (array_agg(client_version ORDER BY started_at DESC))[1] AS version
            FROM session_log sl WHERE member_id IS NOT NULL AND {Person}
            GROUP BY member_id
            """))).ToList();
        var changesBy = (await conn.QueryAsync<(Guid member_id, long n)>(Q(
            """
            SELECT sl.member_id, COUNT(*) AS n
            FROM operations o JOIN session_log sl ON sl.session_id = o.session_id
            WHERE o.created_at > NOW() - make_interval(days => @days) AND sl.member_id IS NOT NULL
            GROUP BY 1
            """))).ToDictionary(r => r.member_id, r => r.n);
        var chatBy = (await conn.QueryAsync<(Guid author_id, long n)>(Q(
            "SELECT author_id, COUNT(*) AS n FROM chat_messages GROUP BY 1"))).ToDictionary(r => r.author_id, r => r.n);
        var commentsBy = (await conn.QueryAsync<(Guid author_id, long n)>(Q(
            "SELECT author_id, COUNT(*) AS n FROM comment_messages WHERE deleted_at IS NULL GROUP BY 1"))).ToDictionary(r => r.author_id, r => r.n);
        var projectsBy = (await conn.QueryAsync<(Guid member_id, string name)>(Q(
            """
            SELECT DISTINCT sl.member_id, p.name FROM session_log sl JOIN projects p ON p.project_id = sl.project_id
            WHERE sl.member_id IS NOT NULL
            """))).GroupBy(r => r.member_id).ToDictionary(g => g.Key, g => g.Select(r => r.name).OrderBy(n => n).ToArray());
        var dailyPeople = await conn.QueryAsync<(DateTime day, long n)>(Q(
            $"""
            SELECT date_trunc('day', started_at AT TIME ZONE 'UTC') AS day, COUNT(DISTINCT member_id) AS n
            FROM session_log sl WHERE started_at > NOW() - make_interval(days => @days) AND member_id IS NOT NULL AND {Person} GROUP BY 1 ORDER BY 1
            """));
        var online = sessions.All.Where(s => s.IsSubscribed).Select(s => s.MemberId).ToHashSet();

        // Storage
        var files = (await conn.QueryAsync<(Guid project_id, string path, long size)>(Q(
            """
            WITH latest AS (
                SELECT DISTINCT ON (o.branch_id, o.target_id) b.project_id, o.type,
                       o.payload->>'s' AS path, COALESCE((o.payload->>'n')::bigint, 0) AS size
                FROM operations o JOIN branches b ON b.branch_id = o.branch_id
                WHERE o.type IN ('RegisterAsset', 'UpdateAsset', 'MoveAsset', 'DeleteAsset')
                ORDER BY o.branch_id, o.target_id, o.seq DESC
            )
            SELECT project_id, path, size FROM latest WHERE type <> 'DeleteAsset' AND path IS NOT NULL
            """))).ToList();
        var dbBytes = await conn.ExecuteScalarAsync<long>(Q("SELECT pg_database_size(current_database())"));

        var now = DateTimeOffset.UtcNow;
        if (now - _usage.At > TimeSpan.FromMinutes(1))
        {
            var (count, bytes) = blobs.Usage();
            _usage = (now, count, bytes);
        }
        long diskFree = 0, diskTotal = 0;
        try
        {
            var drive = new DriveInfo(blobOptions.Root);
            diskFree = drive.AvailableFreeSpace;
            diskTotal = drive.TotalSize;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }

        var h = health.Now();
        return new
        {
            days = Days,
            generated = now,
            activity = new
            {
                daily = activity.Select(a => new { day = Utc(a.day), project = a.project_id, n = a.n }),
                heat = heat.Select(x => new { x.dow, x.hour, x.n }),
                kinds = kinds.Select(k => new { k.type, k.n }),
                totals = new { all = totals.all_ops, today = totals.today, week = totals.week },
            },
            people = new
            {
                list = people.Select(r => new
                {
                    id = r.member_id,
                    name = r.name,
                    sessions = r.sessions,
                    seconds = r.seconds,
                    lastSeen = Utc(r.last_seen),
                    firstSeen = Utc(r.first_seen),
                    version = r.version,
                    online = online.Contains(r.member_id),
                    changes = changesBy.GetValueOrDefault(r.member_id),
                    chat = chatBy.GetValueOrDefault(r.member_id),
                    comments = commentsBy.GetValueOrDefault(r.member_id),
                    projects = projectsBy.GetValueOrDefault(r.member_id, []),
                }).OrderByDescending(r => r.online).ThenByDescending(r => r.lastSeen),
                daily = dailyPeople.Select(d => new { day = Utc(d.day), d.n }),
            },
            storage = new
            {
                stored = new { files = _usage.Files, bytes = _usage.Bytes },
                database = dbBytes,
                disk = new { free = diskFree, total = diskTotal, minFree = blobs.MinFreeBytes },
                maxFile = blobs.MaxBlobBytes,
                byProject = files.GroupBy(f => f.project_id).Select(g => new { project = g.Key, files = g.Count(), bytes = g.Sum(f => f.size) }),
                byKind = files.GroupBy(f => Kind(f.path)).Select(g => new { kind = g.Key, files = g.Count(), bytes = g.Sum(f => f.size) })
                    .OrderByDescending(k => k.bytes),
                biggest = files.OrderByDescending(f => f.size).Take(10).Select(f => new { project = f.project_id, f.path, f.size }),
            },
            health = new
            {
                started = HealthMonitor.Started,
                connections = h.Connections,
                editing = h.Editing,
                memoryMb = Math.Round(h.MemoryMb, 1),
                cpu = h.CpuPercent,
                requests = health.RequestsTotal,
                errors = health.ErrorsTotal,
                slowDisconnects = (sessions as SessionManager)?.SlowDisconnects ?? 0,
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                cores = Environment.ProcessorCount,
                samples = health.Samples().Select(s => new { at = s.At, s.Connections, s.Editing, memoryMb = Math.Round(s.MemoryMb, 1), cpu = s.CpuPercent, s.Requests, s.Errors }),
            },
        };
    }

    // In the editor, over a range: people, time online and changes per
    // bucket, and each person's numbers in the range. Only people who still
    // have an account, and guests from invite codes, count: a deleted account
    // is gone from here too (TeamStore.DeleteUserAsync).
    public static readonly string[] Ranges = ["hour", "day", "week", "month", "quarter", "year", "all"];
    private const string Person = "(sl.via = 'invite code' OR EXISTS (SELECT 1 FROM users u WHERE u.user_id = sl.member_id))";

    public async Task<object> PeopleAsync(string range, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var now = DateTime.UtcNow;
        DateTime first = now;
        if (range == "all")
            first = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT MIN(started_at) FROM session_log sl WHERE sl.member_id IS NOT NULL AND " + Person, cancellationToken: ct)) is { } f
                ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : now;
        var (starts, unit) = Buckets(range, now, first);
        var ends = starts.Skip(1).Append(now).ToArray();
        var from = starts[0];
        var p = new { starts, ends, from, now };
        CommandDefinition Q(string sql) => new(sql, p, commandTimeout: 120, cancellationToken: ct);

        var online = (await conn.QueryAsync<(long i, long people, double seconds)>(Q(
            $"""
            SELECT b.i, COUNT(DISTINCT sl.member_id) AS people,
                   COALESCE(SUM(EXTRACT(EPOCH FROM LEAST(COALESCE(sl.ended_at, sl.last_seen_at), b.e) - GREATEST(sl.started_at, b.s))), 0)::float8 AS seconds
            FROM unnest(@starts::timestamptz[], @ends::timestamptz[]) WITH ORDINALITY AS b(s, e, i)
            JOIN session_log sl ON sl.started_at < b.e AND COALESCE(sl.ended_at, sl.last_seen_at) > b.s
            WHERE sl.member_id IS NOT NULL AND {Person}
            GROUP BY b.i
            """))).ToDictionary(r => r.i);
        var changes = (await conn.QueryAsync<(long i, long n)>(Q(
            """
            SELECT b.i, COUNT(o.op_id) AS n
            FROM unnest(@starts::timestamptz[], @ends::timestamptz[]) WITH ORDINALITY AS b(s, e, i)
            JOIN operations o ON o.created_at >= b.s AND o.created_at < b.e
            GROUP BY b.i
            """))).ToDictionary(r => r.i, r => r.n);

        var people = (await conn.QueryAsync<RangePersonRow>(Q(
            $"""
            SELECT x.*, u.display_name AS account_name, COALESCE(u.is_test, FALSE) AS is_test, u.user_id IS NULL AS guest
            FROM (
                SELECT sl.member_id,
                       (array_agg(sl.member_name ORDER BY sl.started_at DESC))[1] AS name,
                       COUNT(*) AS sessions,
                       COALESCE(SUM(EXTRACT(EPOCH FROM LEAST(COALESCE(sl.ended_at, sl.last_seen_at), @now) - GREATEST(sl.started_at, @from))), 0)::bigint AS seconds,
                       MAX(COALESCE(sl.ended_at, sl.last_seen_at)) AS last_seen,
                       (array_agg(sl.client_version ORDER BY sl.started_at DESC))[1] AS version
                FROM session_log sl
                WHERE sl.member_id IS NOT NULL AND {Person} AND COALESCE(sl.ended_at, sl.last_seen_at) > @from
                GROUP BY sl.member_id
            ) x LEFT JOIN users u ON u.user_id = x.member_id
            """))).ToList();
        var changesBy = (await conn.QueryAsync<(Guid member_id, long n)>(Q(
            """
            SELECT sl.member_id, COUNT(*) AS n FROM operations o JOIN session_log sl ON sl.session_id = o.session_id
            WHERE o.created_at >= @from AND sl.member_id IS NOT NULL GROUP BY 1
            """))).ToDictionary(r => r.member_id, r => r.n);
        var chatBy = (await conn.QueryAsync<(Guid author_id, long n)>(Q(
            "SELECT author_id, COUNT(*) AS n FROM chat_messages WHERE created_at >= @from GROUP BY 1"))).ToDictionary(r => r.author_id, r => r.n);
        var commentsBy = (await conn.QueryAsync<(Guid author_id, long n)>(Q(
            "SELECT author_id, COUNT(*) AS n FROM comment_messages WHERE deleted_at IS NULL AND created_at >= @from GROUP BY 1"))).ToDictionary(r => r.author_id, r => r.n);
        var projectsBy = (await conn.QueryAsync<(Guid member_id, string name)>(Q(
            """
            SELECT DISTINCT sl.member_id, p.name FROM session_log sl JOIN projects p ON p.project_id = sl.project_id
            WHERE sl.member_id IS NOT NULL AND COALESCE(sl.ended_at, sl.last_seen_at) > @from
            """))).GroupBy(r => r.member_id).ToDictionary(g => g.Key, g => g.Select(r => r.name).OrderBy(n => n).ToArray());
        var connected = sessions.All.Where(s => s.IsSubscribed).Select(s => s.MemberId).ToHashSet();

        return new
        {
            range,
            unit,
            from = new DateTimeOffset(from),
            buckets = starts.Select((s, k) => new
            {
                at = new DateTimeOffset(s),
                people = online.TryGetValue(k + 1, out var o) ? o.people : 0,
                hours = online.TryGetValue(k + 1, out var o2) ? Math.Round(Math.Max(0, o2.seconds) / 3600, 2) : 0,
                changes = changes.GetValueOrDefault(k + 1),
            }),
            list = people.Select(r => new
            {
                id = r.member_id,
                name = string.IsNullOrEmpty(r.account_name) ? r.name : r.account_name,
                kind = r.guest ? "guest" : r.is_test ? "test" : "account",
                sessions = r.sessions,
                seconds = Math.Max(0, r.seconds),
                lastSeen = Utc(r.last_seen),
                version = r.version,
                online = connected.Contains(r.member_id),
                changes = changesBy.GetValueOrDefault(r.member_id),
                chat = chatBy.GetValueOrDefault(r.member_id),
                comments = commentsBy.GetValueOrDefault(r.member_id),
                projects = projectsBy.GetValueOrDefault(r.member_id, []),
            }).OrderByDescending(r => r.online).ThenByDescending(r => r.lastSeen),
        };
    }

    // Bucket starts (UTC, oldest first) for a range, and what one bucket is.
    private static (DateTime[] Starts, string Unit) Buckets(string range, DateTime now, DateTime first)
    {
        DateTime[] Every(DateTime start, TimeSpan step, int count) => Enumerable.Range(0, count).Select(k => start + step * k).ToArray();
        var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var today = now.Date;
        switch (range)
        {
            case "hour":
                var fiveMin = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute / 5 * 5, 0, DateTimeKind.Utc);
                return (Every(fiveMin.AddMinutes(-55), TimeSpan.FromMinutes(5), 12), "5min");
            case "day":
                return (Every(hour.AddHours(-23), TimeSpan.FromHours(1), 24), "hour");
            case "week":
                var sixHours = hour.AddHours(-(hour.Hour % 6));
                return (Every(sixHours.AddHours(-6 * 27), TimeSpan.FromHours(6), 28), "6h");
            case "quarter":
                return (Every(today.AddDays(-7 * 12 - (int)(today.DayOfWeek + 6) % 7), TimeSpan.FromDays(7), 13), "week");
            case "year":
                return (Every(today.AddDays(-7 * 51 - (int)(today.DayOfWeek + 6) % 7), TimeSpan.FromDays(7), 52), "week");
            case "all":
                var month = new DateTime(first.Year, first.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var thisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var list = new List<DateTime>();
                for (var m = month; m <= thisMonth; m = m.AddMonths(1)) list.Add(m);
                return (list.ToArray(), "month");
            default: // month
                return (Every(today.AddDays(-29), TimeSpan.FromDays(1), 30), "day");
        }
    }

    // What a file is, for the storage chart.
    public static string Kind(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "tscn" or "scn" => "Scenes",
            "gd" or "cs" or "gdextension" => "Scripts",
            "gdshader" or "gdshaderinc" or "shader" => "Shaders",
            "png" or "jpg" or "jpeg" or "webp" or "svg" or "bmp" or "tga" or "exr" or "hdr" or "ktx" or "dds" => "Images",
            "glb" or "gltf" or "fbx" or "blend" or "obj" or "dae" or "mesh" => "3D models",
            "wav" or "ogg" or "mp3" or "flac" => "Audio",
            "ttf" or "otf" or "woff" or "woff2" or "fnt" => "Fonts",
            "tres" or "res" => "Resources",
            "import" or "uid" => "Import settings",
            "json" or "txt" or "md" or "cfg" or "csv" or "godot" or "ini" or "yml" or "yaml" or "xml" => "Text and config",
            _ => "Other",
        };
    }

    private static DateTimeOffset Utc(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc));

#pragma warning disable IDE1006 // column names
    private sealed class PersonRow
    {
        public Guid member_id { get; init; }
        public string name { get; init; } = "";
        public long sessions { get; init; }
        public long seconds { get; init; }
        public DateTime last_seen { get; init; }
        public DateTime first_seen { get; init; }
        public string version { get; init; } = "";
    }

    private sealed class RangePersonRow
    {
        public Guid member_id { get; init; }
        public string name { get; init; } = "";
        public string? account_name { get; init; }
        public bool is_test { get; init; }
        public bool guest { get; init; }
        public long sessions { get; init; }
        public long seconds { get; init; }
        public DateTime last_seen { get; init; }
        public string version { get; init; } = "";
    }
#pragma warning restore IDE1006
}
