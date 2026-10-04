using System.Net;
using System.Text;
using System.Text.Json;

namespace YHDE.Server.Admin;

// What search engines and link previews (chat apps, social sites) see of each
// page: title, description, preview image and canonical address. They don't
// run scripts, so the server writes these into the page.
public static class SiteMeta
{
    private sealed record Entry(string Title, string Description, bool Index = true);

    private static readonly Dictionary<string, Entry> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/"] = new(
            "YHDE · Real-time collaboration for Godot",
            "Your whole team in the same Godot project at once. Every change shows up for everyone instantly, with no merge conflicts. For Godot 4.7 and newer."),
        ["/docs"] = new(
            "Getting started · YHDE",
            "Install the YHDE add-on, invite your team and work in the same Godot project together, step by step."),
        ["/changelog"] = new("What's new · YHDE", "Every YHDE release and what changed in it."),
        ["/news"] = new("News · YHDE", "Announcements and updates from the YHDE team."),
        ["/status"] = new("Status · YHDE", "Is YHDE working right now? The live state of the servers."),
        ["/contact"] = new("Contact · YHDE", "Questions, support or feedback: how to reach the people behind YHDE."),
        ["/privacy"] = new("Privacy policy · YHDE", "What YHDE stores, why, where, and for how long. No tracking, nothing sold."),
        ["/terms"] = new("Terms of use · YHDE", "The terms for using YHDE: the beta, plans, cancelling, your content."),
        ["/security"] = new("Security · YHDE", "How YHDE protects your projects and account, and how to report a security problem."),
        ["/cookies"] = new("Cookies · YHDE", "The cookies and browser storage YHDE uses. No tracking, analytics or ads."),
        // Private or one-off pages: fine to open, not for search results.
        ["/app"] = new("YHDE", "Your YHDE projects and team.", Index: false),
        ["/join"] = new("Join a project · YHDE", "Join a YHDE project with your invite.", Index: false),
        ["/admin"] = new("Admin · YHDE", "", Index: false),
    };

    private static readonly Entry NotFound = new("Page not found · YHDE", "", Index: false);
    private static readonly Entry Maintenance = new("Back soon · YHDE", "", Index: false);

    // Replaces the page's <title> and fills <!--META-->.
    public static string Apply(string html, HttpContext ctx, int status)
    {
        var req = ctx.Request;
        var path = req.Path.Value is { Length: > 1 } p ? p.TrimEnd('/') : "/";
        if (path.StartsWith("/join/", StringComparison.OrdinalIgnoreCase)) path = "/join";
        var e = status == 503 ? Maintenance : Pages.GetValueOrDefault(path, NotFound);
        // A self-hosted server is not YHDE's site: kept out of search results,
        // its titles name the server, not the product's pitch.
        if (!Site.SiteMode.Official) e = new Entry(SelfHostedTitle(path), "", Index: false);
        var baseUrl = ctx.RequestServices.GetService<Accounts.AccountOptions>()?.BaseUrl(req) ?? $"{req.Scheme}://{req.Host}";
        var url = baseUrl + (path == "/" ? "/" : path);

        var m = new StringBuilder();
        void Tag(string attr, string key, string value) =>
            m.Append($"<meta {attr}=\"{key}\" content=\"{WebUtility.HtmlEncode(value)}\" />\n    ");
        if (e.Description.Length > 0) Tag("name", "description", e.Description);
        if (!e.Index)
        {
            Tag("name", "robots", "noindex");
        }
        else
        {
            m.Append($"<link rel=\"canonical\" href=\"{url}\" />\n    ");
            Tag("property", "og:type", "website");
            Tag("property", "og:site_name", "YHDE");
            Tag("property", "og:title", e.Title);
            Tag("property", "og:description", e.Description);
            Tag("property", "og:url", url);
            Tag("property", "og:locale", "en_US");
            Tag("property", "og:image", baseUrl + "/ui/og.jpg");
            Tag("property", "og:image:width", "1200");
            Tag("property", "og:image:height", "630");
            Tag("property", "og:image:alt", "Several people editing the same Godot scene at the same time, each with their own cursor");
            Tag("name", "twitter:card", "summary_large_image");
            if (path == "/") m.Append(StructuredData(baseUrl));
        }

        html = html.Replace("<!--META-->", m.ToString().TrimEnd());
        var start = html.IndexOf("<title>", StringComparison.Ordinal);
        var end = html.IndexOf("</title>", StringComparison.Ordinal);
        if (start >= 0 && end > start)
            html = html[..start] + $"<title>{WebUtility.HtmlEncode(e.Title)}</title>" + html[(end + 8)..];
        return html;
    }

    private static string SelfHostedTitle(string path)
    {
        var name = Site.SiteMode.Operator.Name.Length > 0 ? Site.SiteMode.Operator.Name : "YHDE server";
        var page = path.Trim('/');
        return page.Length == 0 ? name : $"{char.ToUpperInvariant(page[0])}{page[1..]} · {name}";
    }

    // Tells search engines what YHDE is and what it costs (schema.org), for
    // richer search results. A data block: never run, so the CSP allows it.
    private static string StructuredData(string baseUrl)
    {
        var data = new object[]
        {
            new Dictionary<string, object>
            {
                ["@context"] = "https://schema.org",
                ["@type"] = "SoftwareApplication",
                ["name"] = "YHDE",
                ["url"] = baseUrl + "/",
                ["image"] = baseUrl + "/ui/og.jpg",
                ["description"] = Pages["/"].Description,
                ["applicationCategory"] = "DeveloperApplication",
                ["operatingSystem"] = "Windows, macOS, Linux",
                ["softwareRequirements"] = "Godot 4.7 or newer",
                ["offers"] = new Dictionary<string, object>
                {
                    ["@type"] = "AggregateOffer",
                    ["priceCurrency"] = "EUR",
                    ["lowPrice"] = "3.75",
                    ["highPrice"] = "30.00",
                    ["offerCount"] = 4,
                },
            },
            new Dictionary<string, object>
            {
                ["@context"] = "https://schema.org",
                ["@type"] = "Organization",
                ["name"] = "YHDE",
                ["url"] = baseUrl + "/",
                ["logo"] = baseUrl + "/ui/icon-512.png",
                ["email"] = "contact@frostinteractive.fi",
            },
        };
        var json = JsonSerializer.Serialize(data).Replace("<", "\\u003c");
        return $"<script type=\"application/ld+json\">{json}</script>\n    ";
    }

    // What llms.txt says about plans: the beta until 1.0, then the paid plans.
    private static string PlanText() => Teams.Plans.Open
        ? "YHDE is hosted: the team owner pays for a plan and everyone they invite joins free. Plans are Solo (1 person), Trio (3), Team (6) and Studio (12), from €3.75 a month including VAT; extra seats can be added one at a time."
        : "YHDE is in beta and free to use. Anyone can make up to three projects and invite three people into each. Paid plans start with version 1.0.";

    public static void MapSeo(this WebApplication app)
    {
        app.MapMethods("/robots.txt", UiPages.GetHead, (HttpRequest req, Accounts.AccountOptions o) => !Site.SiteMode.Official
            ? Results.Text("User-agent: *\nDisallow: /\n", "text/plain; charset=utf-8")
            : Results.Text(
            "User-agent: *\n" +
            "Disallow: /admin\nDisallow: /api/\nDisallow: /auth/\nDisallow: /addon/\nDisallow: /ws\n\n" +
            $"Sitemap: {o.BaseUrl(req)}/sitemap.xml\n", "text/plain; charset=utf-8"));

        app.MapMethods("/sitemap.xml", UiPages.GetHead, (HttpRequest req, Accounts.AccountOptions o) =>
        {
            var b = o.BaseUrl(req);
            var x = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            x.Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
            foreach (var (path, e) in Pages)
                if (e.Index && Site.SiteMode.Official) x.Append($"  <url><loc>{WebUtility.HtmlEncode(b + path)}</loc></url>\n");
            x.Append("</urlset>\n");
            return Results.Text(x.ToString(), "application/xml; charset=utf-8");
        });

        // How to report a security problem (RFC 9116). Expires with the
        // current domain; update both when moving to the new one.
        app.MapMethods("/.well-known/security.txt", UiPages.GetHead, (HttpRequest req, Accounts.AccountOptions o) => Results.Text($"""
            Contact: mailto:{(Site.SiteMode.Official ? "support@frostinteractive.fi" : Site.SiteMode.Operator.Email.Length > 0 ? Site.SiteMode.Operator.Email : "support@frostinteractive.fi")}
            Expires: 2027-02-06T00:00:00Z
            Preferred-Languages: en
            Canonical: {o.BaseUrl(req)}/.well-known/security.txt
            Policy: {o.BaseUrl(req)}/security

            """, "text/plain; charset=utf-8"));

        // A plain summary for AI assistants and their crawlers (llmstxt.org).
        app.MapMethods("/llms.txt", UiPages.GetHead, (HttpRequest req, Accounts.AccountOptions o) =>
        {
            if (!Site.SiteMode.Official) return Results.NotFound();
            var b = o.BaseUrl(req);
            return Results.Text($$"""
                # YHDE

                > Real-time collaboration for the Godot game engine. An editor add-on puts a whole team in the same Godot project at the same time: every change (scenes, properties, scripts, files) shows up for everyone at once, with live cursors and no merge conflicts. For Godot 4.7 and newer, on Windows, macOS and Linux.

                {{PlanText()}} Projects are stored in the EU (Helsinki, Finland). YHDE never uses its users' games, files or messages to train AI models.

                ## Pages

                - [Home]({{b}}/): what YHDE does, plans and prices, questions and answers
                - [Getting started]({{b}}/docs): install the add-on, start a team, invite people, work together
                - [What's new]({{b}}/changelog): every release
                - [Status]({{b}}/status): whether the servers are up
                - [Contact]({{b}}/contact): support, billing, privacy
                - [Privacy policy]({{b}}/privacy)
                - [Terms of use]({{b}}/terms)
                """, "text/plain; charset=utf-8");
        });

        app.MapMethods("/humans.txt", UiPages.GetHead, () => Results.Text("""
            /* TEAM */
            YHDE, made in Finland.
            Contact: contact@frostinteractive.fi

            /* SITE */
            Built with: React, Tailwind CSS, ASP.NET Core, PostgreSQL, Caddy
            Font: Bricolage Grotesque

            """, "text/plain; charset=utf-8"));

        // Browsers ask for this on their own; send them the real icon.
        app.MapMethods("/favicon.ico", UiPages.GetHead, () => Results.Redirect("/ui/favicon.ico", permanent: true));
    }
}
