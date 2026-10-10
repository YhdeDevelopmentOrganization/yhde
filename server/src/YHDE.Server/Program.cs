using Microsoft.AspNetCore.HttpOverrides;
using System.Net.WebSockets;
using Npgsql;
using YHDE.Server.Addon;
using YHDE.Server.Admin;
using YHDE.Server.Assets;
using YHDE.Server.Gateway;
using YHDE.Server.Operations;
using YHDE.Server.Persistence;
using YHDE.Server.Persistence.Migrations;
using YHDE.Server.Persistence.Repositories;
using YHDE.Server.Presence;
using YHDE.Server.Projects;
using YHDE.Server.Social;
using YHDE.Server.Text;

var builder = WebApplication.CreateBuilder(args);
// Official YHDE site or a self-hosted server (Site/SiteMode.cs): decides the
// website, the plans and what search engines see.
YHDE.Server.Site.SiteMode.Configure(builder.Configuration);

// Configuration
var connectionString = builder.Configuration["Yhde:ConnectionString"]
    ?? throw new InvalidOperationException("Yhde:ConnectionString is required.");

// Database
// Stay below Postgres' connection limit (100 by default, 3 reserved): with
// more, bursts fail instead of waiting for a free connection.
var connectionBuilder = new NpgsqlConnectionStringBuilder(connectionString);
if (!connectionString.Contains("Pool Size", StringComparison.OrdinalIgnoreCase)) connectionBuilder.MaxPoolSize = 50;
var dataSource = new NpgsqlDataSourceBuilder(connectionBuilder.ConnectionString).Build();
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton<Database>();

// Repositories
builder.Services.AddSingleton<IOperationRepository, OperationRepository>();
builder.Services.AddSingleton<IBranchRepository, BranchRepository>();
builder.Services.AddSingleton<ProjectRepository>();
builder.Services.AddSingleton<IProjectStore, ProjectStore>();

// Session manager (ephemeral: never persisted)
builder.Services.AddSingleton<ISessionManager, SessionManager>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PresenceService>();
builder.Services.AddSingleton<AccessGate>();
builder.Services.AddSingleton<AdminAuth>();
builder.Services.AddSingleton<SessionLog>();
builder.Services.AddSingleton<HealthMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthMonitor>());
builder.Services.AddSingleton<AdminStats>();
builder.Services.AddSingleton<ProjectImporter>();
builder.Services.AddSingleton<ServerUpdates>();

// Chat and comments (stored, never part of the operation log)
builder.Services.AddSingleton<ISocialStore, SocialStore>();
builder.Services.AddSingleton<SocialService>();

// Asset bytes (content-addressed blob store on disk)
builder.Services.AddSingleton(BlobStoreOptions.From(builder.Configuration, builder.Environment));
builder.Services.AddSingleton<BlobStore>();
builder.Services.AddSingleton<IProjectBlobs, ProjectBlobs>();
builder.Services.AddHostedService<BlobJanitor>();
builder.Services.AddHostedService<SessionSweeper>();
builder.Services.AddSingleton(sp => AddonStore.From(builder.Configuration, sp.GetRequiredService<BlobStoreOptions>()));
builder.Services.AddSingleton<YHDE.Server.Site.SiteStore>();

// Accounts and sign-in (ADR 0015).
builder.Services.AddSingleton(YHDE.Server.Accounts.AccountOptions.From(builder.Configuration));
builder.Services.AddSingleton<YHDE.Server.Accounts.AccountStore>();
builder.Services.AddSingleton<YHDE.Server.Accounts.AccountService>();
builder.Services.AddSingleton<YHDE.Server.Accounts.OAuth>();
builder.Services.AddSingleton<YHDE.Server.Teams.TeamStore>();
builder.Services.AddSingleton<YHDE.Server.Teams.StorageQuota>();
builder.Services.AddSingleton<YHDE.Server.Accounts.EditorTokens>();
builder.Services.AddSingleton<YHDE.Server.Teams.PromoStore>();
builder.Services.AddSingleton<StaffStore>();
builder.Services.AddHttpClient("oauth", c => c.Timeout = TimeSpan.FromSeconds(15));
if (!string.IsNullOrWhiteSpace(builder.Configuration["Yhde:Smtp:Host"]))
    builder.Services.AddSingleton<YHDE.Server.Accounts.IEmailSender, YHDE.Server.Accounts.SmtpEmailSender>();
else
    builder.Services.AddSingleton<YHDE.Server.Accounts.IEmailSender, YHDE.Server.Accounts.LogEmailSender>();

// Core pipeline
builder.Services.AddSingleton<TextDocuments>();
builder.Services.AddSingleton<BranchCommitter>();
builder.Services.AddSingleton<OperationProcessor>();
builder.Services.AddSingleton<UndoService>();
builder.Services.AddSingleton<WebSocketGateway>();

// WebSocket support (built into ASP.NET Core)
builder.Services.AddHealthChecks();

// A ceiling per address on the website's API, sign-in and join pages, so no
// single client can flood them (security.md). Well above what a person
// clicking around needs; editors' file routes (/assets, /blobs) and the
// WebSocket are not counted. Finer limits per account and per action sit in
// the endpoints themselves.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var p = ctx.Request.Path;
        var counted = p.StartsWithSegments("/api") || p.StartsWithSegments("/auth") || p.StartsWithSegments("/join")
            || p.StartsWithSegments("/admin/api") || p.StartsWithSegments("/early-access");
        if (!counted) return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("free");
        var who = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(who, _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new { title = "Too many requests", status = 429, detail = "Too many requests from here. Wait a minute and try again." }, ct);
    };
});

// Behind the server kit's Caddy every request comes from Caddy's address.
// Take the client address from X-Forwarded-For, but only when a proxy on a
// private network (Docker, the same machine) sent it, so a client cannot
// claim another address (the admin lockout counts per address).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    foreach (var network in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7" })
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseRateLimiter();

// Requests and server errors, for the admin page's health charts.
app.Use(async (ctx, next) =>
{
    var health = ctx.RequestServices.GetRequiredService<HealthMonitor>();
    try
    {
        await next();
        health.CountRequest(ctx.Response.StatusCode >= 500);
    }
    catch
    {
        health.CountRequest(true);
        throw;
    }
});

// Fail at startup, not on the first connection, if access or storage is misconfigured.
var accessGate = app.Services.GetRequiredService<AccessGate>();
app.Services.GetRequiredService<BlobStore>();

// Run migrations before accepting connections
// Migrations are append-only DDL; safe to run on every startup (idempotent).
var migrationLogger = app.Services.GetRequiredService<ILogger<Program>>();
Runner.Run(connectionString, migrationLogger);

// WebSocket middleware
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("Yhde:HeartbeatIntervalSeconds", 30)),
    // A peer that does not answer the keep-alive within this is gone.
    KeepAliveTimeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("Yhde:IdleTimeoutSeconds", 60)),
});

// Health check
app.MapHealthChecks("/health");

// Asset transfer (HTTP, same access key)
app.MapAssetEndpoints();

// Admin page (off unless Yhde:AdminPassword is set)
app.Services.GetRequiredService<AdminAuth>();
app.MapUi();
app.MapAdminEndpoints();

// Onboarding: join page, the editor add-on and its updates
app.MapAddonEndpoints();

// Website content: news, release notes, announcement, early access
YHDE.Server.Site.SiteEndpoints.MapSiteEndpoints(app);
YHDE.Server.Accounts.AuthEndpoints.MapAuthEndpoints(app);
YHDE.Server.Teams.TeamEndpoints.MapTeamEndpoints(app);
YHDE.Server.Accounts.DeviceEndpoints.MapDeviceEndpoints(app);

// WebSocket endpoint. AccessGate admits the connection before the upgrade;
// the project and branch come later, in Subscribe.
// Per address: new connections, and refused keys (an editor with a wrong
// key stops retrying; anything else trying many keys is slowed). This
// computer is exempt, for tests and a server used on its own machine.
var wsUpgrades = new YHDE.Server.Accounts.Limiter(60, TimeSpan.FromMinutes(1));
var wsRefusals = new YHDE.Server.Accounts.Limiter(20, TimeSpan.FromMinutes(10));
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("WebSocket endpoint. Use the ws:// or wss:// scheme.");
        return;
    }

    var remote = ctx.Connection.RemoteIpAddress;
    var address = remote?.ToString() ?? "";
    var limited = remote is not null && !System.Net.IPAddress.IsLoopback(remote);
    if (limited && (wsRefusals.Blocked(address) || !wsUpgrades.Try(address)))
    {
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.Response.Headers.RetryAfter = "60";
        return;
    }

    var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
    var socket = await ctx.WebSockets.AcceptWebSocketAsync();

    // A refused key is reported with a close code rather than an HTTP 401 so
    // the editor can tell "wrong key" from "server unreachable" and stop
    // retrying (network_protocol.md). Nothing from the peer is read first.
    var grant = await accessGate.AdmitAsync(authHeader, ctx.RequestAborted);
    if (grant is null)
    {
        if (limited) wsRefusals.Try(address);
        app.Logger.LogWarning("Refused a connection from {Remote}: missing or wrong access key",
            ctx.Connection.RemoteIpAddress);
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        closing.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await socket.CloseAsync((WebSocketCloseStatus)AccessGate.UnauthorizedCloseCode,
                "access key or invite code rejected", closing.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            socket.Abort(); // peer gone or never acknowledged the close
        }
        return;
    }

    var gateway = ctx.RequestServices.GetRequiredService<WebSocketGateway>();
    await gateway.HandleAsync(socket, authHeader, grant, ctx.RequestAborted);
});

app.Run();

// Expose the type for integration tests (WebApplicationFactory).
public partial class Program { }
