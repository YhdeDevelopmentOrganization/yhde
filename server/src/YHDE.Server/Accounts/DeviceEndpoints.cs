using System.Collections.Concurrent;
using System.Security.Cryptography;
using YHDE.Server.Gateway;
using YHDE.Server.Presence;
using YHDE.Server.Projects;
using YHDE.Server.Teams;

namespace YHDE.Server.Accounts;

// Signing in to YHDE from the Godot editor (ADR 0015, device flow):
//
//   1. Godot:   POST /api/device/start {device}   -> a secret device code for
//               Godot, a short user code, and the page to open.
//   2. Browser: the person opens /app#/device?code=ABCD-EFGH, signs in if
//               needed and presses Allow (POST /api/device/approve).
//   3. Godot:   POST /api/device/poll {deviceCode} every few seconds until
//               it gets a sign-in token (an "editor" session, 90 days).
//
// No password or OAuth ever passes through the editor. Godot then uses the
// token for the WebSocket and for
//   GET  /api/editor/projects   -> who you are, your team and its projects
//   POST /api/editor/sign-out   -> ends this editor's sign-in
public static class DeviceEndpoints
{
    public sealed record StartRequest(string? Device);
    public sealed record PollRequest(string? DeviceCode);
    public sealed record ApproveRequest(string? Code, bool Allow);

    private sealed class Pending
    {
        public required string DeviceHash { get; init; }
        public required string Device { get; init; }
        public required string Ip { get; init; }
        public required DateTimeOffset Expires { get; init; }
        public Guid? ApprovedBy { get; set; }
        public bool Denied { get; set; }
    }

    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan EditorSessionLifetime = TimeSpan.FromDays(90);
    private const int PollSeconds = 3;

    // By user code (what the person sees), with the device code's hash inside.
    private static readonly ConcurrentDictionary<string, Pending> ByUserCode = new(StringComparer.Ordinal);
    // Device code hash -> user code, for polling.
    private static readonly ConcurrentDictionary<string, string> ByDevice = new(StringComparer.Ordinal);
    private static readonly Limiter Starts = new(10, TimeSpan.FromHours(1));
    private static readonly Limiter Polls = new(400, TimeSpan.FromMinutes(10));
    private static readonly Limiter Approvals = new(20, TimeSpan.FromHours(1));
    private static readonly Limiter Lookups = new(30, TimeSpan.FromMinutes(10));

    // 8 characters from an alphabet without look-alikes (no 0/O, 1/I/L, U),
    // shown as XXXX-XXXX: about 39 bits, and it lives 10 minutes.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

    private static string NewUserCode()
    {
        Span<char> c = stackalloc char[8];
        for (var i = 0; i < 8; i++) c[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return $"{c[..4]}-{c[4..]}";
    }

    public static string NormalizeUserCode(string? code)
    {
        var s = new string((code ?? "").ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return s.Length == 8 ? $"{s[..4]}-{s[4..]}" : "";
    }

    private static void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (k, v) in ByUserCode)
            if (v.Expires <= now && ByUserCode.TryRemove(k, out _)) ByDevice.TryRemove(v.DeviceHash, out _);
    }

    private static void Forget(string deviceHash)
    {
        if (ByDevice.TryRemove(deviceHash, out var userCode)) ByUserCode.TryRemove(userCode, out _);
    }

    private static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
        {
            var ctx = context.HttpContext;
            // Godot and the website both send this header; other sites can't.
            if (!HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Headers[AuthEndpoints.CsrfHeader] != "1")
                return Results.Problem("Missing request header.", statusCode: 403);
            ctx.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        api.MapPost("/device/start", (StartRequest r, HttpContext ctx, AccountOptions options) =>
        {
            if (!Starts.Try(Ip(ctx))) return Results.Problem("Too many sign-in attempts from here. Wait a while and try again.", statusCode: 429);
            if (ByUserCode.Count > 5_000) Sweep();
            if (ByUserCode.Count > 20_000) return Results.Problem("Too many sign-ins at once. Try again in a few minutes.", statusCode: 503);
            var deviceCode = Secrets.NewToken();
            string userCode;
            do userCode = NewUserCode(); while (ByUserCode.ContainsKey(userCode));
            var deviceHash = Convert.ToHexString(Secrets.Hash(deviceCode));
            ByDevice[deviceHash] = userCode;
            ByUserCode[userCode] = new Pending
            {
                DeviceHash = deviceHash,
                Device = NameRules.Clean(r.Device, NameRules.Kind.Label) is { Length: > 0 } d ? d : "Godot editor",
                Ip = Ip(ctx),
                Expires = DateTimeOffset.UtcNow + CodeLifetime,
            };
            var baseUrl = options.BaseUrl(ctx.Request);
            return Results.Ok(new
            {
                deviceCode,
                userCode,
                url = $"{baseUrl}/app#/device?code={userCode}",
                interval = PollSeconds,
                expiresIn = (int)CodeLifetime.TotalSeconds,
            });
        });

        api.MapPost("/device/poll", async (PollRequest r, HttpContext ctx, AccountStore accounts, CancellationToken ct) =>
        {
            if (!Polls.Try(Ip(ctx))) return Results.Problem("Too many requests. Wait a minute.", statusCode: 429);
            if (string.IsNullOrEmpty(r.DeviceCode)) return Results.Problem("No device code.", statusCode: 400);
            var hash = Convert.ToHexString(Secrets.Hash(r.DeviceCode));
            // Looked up by the code's hash, so no timing reveals partial matches.
            if (!ByDevice.TryGetValue(hash, out var userCode) || !ByUserCode.TryGetValue(userCode, out var p) || p.Expires <= DateTimeOffset.UtcNow)
            {
                Forget(hash);
                return Results.Ok(new { status = "expired" });
            }
            if (p.Denied)
            {
                Forget(hash);
                return Results.Ok(new { status = "denied" });
            }
            if (p.ApprovedBy is not { } userId) return Results.Ok(new { status = "pending" });
            // The token is handed out once.
            if (!ByDevice.TryRemove(hash, out _)) return Results.Ok(new { status = "expired" });
            ByUserCode.TryRemove(userCode, out _);
            var token = await accounts.CreateSessionAsync(userId, "editor", p.Device, Ip(ctx), EditorSessionLifetime, ct);
            var user = await accounts.ByIdAsync(userId, ct);
            return Results.Ok(new { status = "approved", token, name = user?.Name ?? "", email = user?.Email ?? "" });
        });

        // The website's side: what is asking, and the person's answer.
        api.MapGet("/device/{code}", async (string code, HttpContext ctx, AccountStore accounts, CancellationToken ct) =>
        {
            if (await AuthEndpoints.CurrentAsync(ctx, accounts, ct) is not { } s) return Results.Problem("Not signed in.", statusCode: 401);
            if (!Lookups.Try(s.User.Id.ToString())) return Results.Problem("Too many tries. Wait a few minutes.", statusCode: 429);
            var key = NormalizeUserCode(code);
            if (!ByUserCode.TryGetValue(key, out var p) || p.Expires <= DateTimeOffset.UtcNow || p.Denied || p.ApprovedBy is not null)
                return Results.Problem("This sign-in code has expired or was already used. Press Sign in in Godot again.", statusCode: 404);
            return Results.Ok(new { code = key, device = p.Device, expires = p.Expires });
        });

        api.MapPost("/device/approve", async (ApproveRequest r, HttpContext ctx, AccountStore accounts, AccountOptions options, CancellationToken ct) =>
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !string.Equals(origin, options.BaseUrl(ctx.Request), StringComparison.OrdinalIgnoreCase))
                return Results.Problem("This request came from another site.", statusCode: 403);
            if (await AuthEndpoints.CurrentAsync(ctx, accounts, ct) is not { } s) return Results.Problem("Not signed in.", statusCode: 401);
            if (!Approvals.Try(s.User.Id.ToString())) return Results.Problem("Too many tries. Wait a while.", statusCode: 429);
            if (!s.User.EmailVerified) return Results.Problem("Confirm your email address first: open the link we sent you.", statusCode: 403);
            var key = NormalizeUserCode(r.Code);
            if (!ByUserCode.TryGetValue(key, out var p) || p.Expires <= DateTimeOffset.UtcNow || p.Denied || p.ApprovedBy is not null)
                return Results.Problem("This sign-in code has expired or was already used. Press Sign in in Godot again.", statusCode: 404);
            if (r.Allow) p.ApprovedBy = s.User.Id;
            else p.Denied = true;
            return Results.Ok(new { ok = true });
        });

        // For the signed-in editor

        api.MapGet("/editor/projects", async (HttpContext ctx, AccountStore accounts, TeamStore teams, IProjectStore projects,
            ISessionManager sessions, IConfiguration config, CancellationToken ct) =>
        {
            if (await EditorAsync(ctx, accounts, ct) is not { } s) return Results.Problem("Sign in again.", statusCode: 401);
            var owned = await TeamEndpoints.OwnedOrOpenAsync(teams, config, s.User, ct);
            var mine = await teams.ProjectsForAsync(s.User.Id, ct);
            var ids = mine.Select(m => m.ProjectId).ToList();
            var stats = (await teams.StatsAsync(ids, ct)).ToDictionary(x => x.ProjectId);
            // Each owner's storage, so the panel can warn before it runs out.
            var storage = new Dictionary<Guid, (long Used, long Limit, string Owner)>();
            foreach (var teamId in mine.Select(m => m.TeamId).Distinct())
                if (await teams.GetAsync(teamId, ct) is { } t)
                {
                    var theirs = await teams.ProjectIdsAsync(teamId, ct);
                    var used = (await teams.StatsAsync(theirs, ct)).Sum(x => x.FileBytes);
                    var owner = await accounts.ByIdAsync(t.OwnerId, ct);
                    storage[teamId] = (used, t.StorageBytes, owner?.Name ?? "");
                }
            var list = new List<object>();
            foreach (var m in mine)
            {
                if (await projects.GetAsync(m.ProjectId, ct) is not { } p) continue;
                var st = stats.GetValueOrDefault(m.ProjectId);
                var (used, limit, ownerName) = storage.GetValueOrDefault(m.TeamId);
                list.Add(new
                {
                    id = m.ProjectId,
                    name = p.Name,
                    branchId = p.MainBranchId,
                    archived = p.ArchivedAt is not null,
                    files = st?.Files ?? 0,
                    bytes = st?.FileBytes ?? 0,
                    changes = st?.Operations ?? 0,
                    online = sessions.GetProjectSessions(m.ProjectId).Select(x => x.MemberName).Distinct().Take(12).ToArray(),
                    access = m.Access,
                    role = m.Role,
                    owner = ownerName,
                    storageUsed = used,
                    storageLimit = limit,
                });
            }
            return Results.Ok(new
            {
                user = new { id = s.User.Id, name = s.User.Name, email = s.User.Email },
                // Add-on 0.4 shows this; there are no teams any more.
                team = owned is { } o ? new { id = o.Id, name = s.User.Name, plan = o.PlanInfo.Name, role = "owner" } : null,
                projects = list,
            });
        });

        api.MapPost("/editor/sign-out", async (HttpContext ctx, AccountStore accounts, EditorTokens editors, CancellationToken ct) =>
        {
            if (Bearer(ctx) is { } token) await accounts.RevokeTokenAsync(token, ct);
            editors.Forget();
            return Results.Ok(new { ok = true });
        });
    }

    private static string? Bearer(HttpContext ctx)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
    }

    private static async Task<(UserSession Session, User User)?> EditorAsync(HttpContext ctx, AccountStore accounts, CancellationToken ct) =>
        Bearer(ctx) is { } token && EditorTokens.LooksLikeToken(token) ? await accounts.SessionAsync(token, "editor", ct) : null;
}
