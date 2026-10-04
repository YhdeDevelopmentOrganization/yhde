using System.Text.Json;
using Microsoft.Extensions.FileProviders;

namespace YHDE.Server.Admin;

// The web pages (home, dashboard, join, admin): a React app (server/admin-ui, built with
// Vite into ui/ next to the server). Pages are served with a strict
// Content-Security-Policy; the join page gets what to show as a JSON block
// (never run as script).
public static class UiPages
{
    private const string Csp =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string _root = "";
    internal static readonly string[] GetHead = ["GET", "HEAD"];

    public static void MapUi(this WebApplication app)
    {
        _root = app.Configuration["Yhde:UiPath"] is { Length: > 0 } p ? Path.GetFullPath(p) : Path.Combine(AppContext.BaseDirectory, "ui");

        // The home page and the team dashboard. The dashboard's pages are
        // hash routes, so /app is the only path it needs.
        // HEAD too: link checkers and preview bots often ask with it first.
        // A self-hosted server has no marketing page: its home is a plain
        // page with its operator and sign-in (site.html picks it from "/").
        app.MapMethods("/", GetHead, () => Page(YHDE.Server.Site.SiteMode.Official ? "home.html" : "site.html"));
        app.MapMethods("/app", GetHead, () => Page("app.html"));
        app.MapSeo();

        // Content pages share one file; it picks the page from the path.
        foreach (var path in new[] { "/privacy", "/terms", "/security", "/cookies", "/contact", "/status", "/changelog", "/docs", "/news" })
            app.MapMethods(path, GetHead, () => Page("site.html"));

        // Anything else a browser asks for gets the 404 page. API, asset and
        // add-on paths keep a plain 404 so programs are not handed HTML.
        app.MapFallback((HttpContext ctx) =>
        {
            var p = ctx.Request.Path;
            var api = p.StartsWithSegments("/api") || p.StartsWithSegments("/admin/api") || p.StartsWithSegments("/assets") || p.StartsWithSegments("/addon")
                || p.StartsWithSegments("/ui") || p.StartsWithSegments("/ws") || p.StartsWithSegments("/health") || p.StartsWithSegments("/media") || p.StartsWithSegments("/auth");
            return api || !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)) ? Results.NotFound() : Page("site.html", null, 404);
        });

        if (!Directory.Exists(_root))
        {
            app.Logger.LogWarning("The admin and join pages are not built ({Root} is missing): run npm run build in server/admin-ui", _root);
            return;
        }
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(_root),
            RequestPath = "/ui",
            OnPrepareResponse = ctx =>
            {
                var h = ctx.Context.Response.Headers;
                h.XContentTypeOptions = "nosniff";
                // Built files (ui/assets) have content hashes in their names;
                // the rest, like the demo video, can be replaced in place.
                h.CacheControl = ctx.Context.Request.Path.StartsWithSegments("/ui/assets") ? "public, max-age=31536000, immutable" : "no-cache";
            },
        });
    }

    public static IResult Page(string name, object? data = null, int status = 200) => new PageResult(name, data, status);

    private sealed class PageResult(string name, object? data, int status) : IResult
    {
        public async Task ExecuteAsync(HttpContext ctx)
        {
            var page = name;
            var pageData = data;
            var code = status;
            // Maintenance mode (switched on the admin page): visitors get the
            // "back soon" page; the admin page and editors keep working.
            if (name != "admin.html" && ctx.RequestServices.GetService<YHDE.Server.Site.SiteStore>() is { } site
                && (await site.SettingsAsync(ctx.RequestAborted)).Maintenance is { Enabled: true } maintenance)
            {
                page = "site.html";
                pageData = new { maintenance = maintenance.Message };
                code = 503;
                ctx.Response.Headers.RetryAfter = "600";
            }
            var file = Path.Combine(_root, page);
            if (!File.Exists(file))
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsync("This page is not built on this server (server/admin-ui: npm run build).");
                return;
            }
            var html = await File.ReadAllTextAsync(file);
            if (pageData is not null)
            {
                // "<" is escaped so the JSON can never close the script element.
                var json = JsonSerializer.Serialize(pageData, Json).Replace("<", "\\u003c");
                html = html.Replace("<!--PAGE_DATA-->", $"<script id=\"page-data\" type=\"application/json\">{json}</script>");
            }
            html = SiteMeta.Apply(html, ctx, code);
            // Every page learns whether this is YHDE's site or a self-hosted
            // server, and who runs it (a data block: never run as script).
            var mode = JsonSerializer.Serialize(YHDE.Server.Site.SiteMode.PageInfo(), Json).Replace("<", "\\u003c");
            html = html.Replace("</head>", $"<script id=\"site-mode\" type=\"application/json\">{mode}</script>\n  </head>");
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var h = ctx.Response.Headers;
            h.ContentSecurityPolicy = Csp;
            h.XContentTypeOptions = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h.CacheControl = "no-store";
            if (HttpMethods.IsHead(ctx.Request.Method)) return;
            await ctx.Response.WriteAsync(html);
        }
    }
}
