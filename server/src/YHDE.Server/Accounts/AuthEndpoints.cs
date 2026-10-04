namespace YHDE.Server.Accounts;

// Signing in on the website (ADR 0015).
//   POST /api/auth/register {email, password, name}   -> always "check your email"
//   POST /api/auth/login {email, password}             -> session cookie
//   POST /api/auth/logout
//   GET  /api/auth/me                                  -> the signed-in person, or 401
//   GET  /api/auth/providers                           -> which of GitHub/Google are set up
//   POST /api/auth/verify {token}, /api/auth/verify/resend
//   POST /api/auth/forgot {email}, /api/auth/reset {token, password}
//   POST /api/auth/password {current, next}, /api/auth/profile {name}
//   GET  /api/auth/sessions, POST /api/auth/sessions/{id}/revoke
//   POST /api/auth/providers/{provider}/unlink
//   GET  /auth/{provider}?link=1&return=/app#/…         -> to GitHub/Google
//   GET  /auth/{provider}/callback                      -> back from them
// Every POST needs the X-YHDE header and, when the browser sends one, an
// Origin of this site: other sites can do neither (CSRF).
public static class AuthEndpoints
{
    public const string SessionCookie = "yhde_sid";
    public const string CsrfHeader = "X-YHDE";

    public sealed record Credentials(string? Email, string? Password, string? Name);
    public sealed record TokenRequest(string? Token, string? Password);
    public sealed record EmailRequest(string? Email);
    public sealed record PasswordChange(string? Current, string? Next);
    public sealed record ProfileChange(string? Name);

    private static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    private static string Device(HttpContext ctx) => ctx.Request.Headers.UserAgent.ToString();

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/auth").AddEndpointFilter(async (context, next) =>
        {
            var ctx = context.HttpContext;
            if (!HttpMethods.IsGet(ctx.Request.Method))
            {
                if (ctx.Request.Headers[CsrfHeader] != "1") return Results.Problem("Missing request header.", statusCode: 403);
                var origin = ctx.Request.Headers.Origin.ToString();
                var options = ctx.RequestServices.GetRequiredService<AccountOptions>();
                if (origin.Length > 0 && !string.Equals(origin, options.BaseUrl(ctx.Request), StringComparison.OrdinalIgnoreCase))
                    return Results.Problem("This request came from another site.", statusCode: 403);
            }
            ctx.Response.Headers.CacheControl = "no-store";
            try
            {
                return await next(context);
            }
            catch (AccountService.Refused r)
            {
                return Results.Problem(r.Message, statusCode: r.Status);
            }
        });

        api.MapGet("/providers", (OAuth oauth) => Results.Ok(new { github = oauth.Enabled("github"), google = oauth.Enabled("google") }));

        api.MapPost("/register", async (Credentials c, HttpContext ctx, AccountService accounts, AccountOptions options, CancellationToken ct) =>
        {
            await accounts.RegisterAsync(c.Email, c.Password, c.Name, Ip(ctx), options.BaseUrl(ctx.Request), ct);
            return Results.Ok(new { checkEmail = true });
        });

        api.MapPost("/login", async (Credentials c, HttpContext ctx, AccountService accounts, AccountStore store, CancellationToken ct) =>
        {
            var user = await accounts.LoginAsync(c.Email, c.Password, Ip(ctx), ct);
            await SignInAsync(ctx, store, user, ct);
            return Results.Ok(await MeAsync(store, user, ct));
        });

        api.MapPost("/logout", async (HttpContext ctx, AccountStore store, CancellationToken ct) =>
        {
            if (ctx.Request.Cookies[SessionCookie] is { Length: > 0 } token) await store.RevokeTokenAsync(token, ct);
            ctx.Response.Cookies.Delete(SessionCookie, CookieOptions(ctx));
            return Results.Ok(new { ok = true });
        });

        api.MapGet("/me", async (HttpContext ctx, AccountStore store, CancellationToken ct) =>
            await CurrentAsync(ctx, store, ct) is { } s ? Results.Ok(await MeAsync(store, s.User, ct)) : Results.Problem("Not signed in.", statusCode: 401));

        api.MapPost("/verify", async (TokenRequest r, AccountService accounts, CancellationToken ct) =>
            await accounts.VerifyAsync(r.Token, ct)
                ? Results.Ok(new { ok = true })
                : Results.Problem("This link has expired or was already used. Sign in and ask for a new one.", statusCode: 400));

        api.MapPost("/verify/resend", async (HttpContext ctx, AccountStore store, AccountService accounts, AccountOptions options, CancellationToken ct) =>
        {
            if (await CurrentAsync(ctx, store, ct) is not { } s) return Results.Problem("Not signed in.", statusCode: 401);
            await accounts.SendVerificationAsync(s.User, options.BaseUrl(ctx.Request), ct);
            return Results.Ok(new { ok = true });
        });

        api.MapPost("/forgot", async (EmailRequest r, HttpContext ctx, AccountService accounts, AccountOptions options, CancellationToken ct) =>
        {
            await accounts.ForgotAsync(r.Email, Ip(ctx), options.BaseUrl(ctx.Request), ct);
            return Results.Ok(new { ok = true });
        });

        api.MapPost("/reset", async (TokenRequest r, HttpContext ctx, AccountService accounts, AccountStore store, CancellationToken ct) =>
        {
            var user = await accounts.ResetAsync(r.Token, r.Password, ct);
            await SignInAsync(ctx, store, user, ct);
            return Results.Ok(await MeAsync(store, user, ct));
        });

        api.MapPost("/password", async (PasswordChange r, HttpContext ctx, AccountStore store, AccountService accounts, CancellationToken ct) =>
        {
            if (await CurrentAsync(ctx, store, ct) is not { } s) return Results.Problem("Not signed in.", statusCode: 401);
            await accounts.ChangePasswordAsync(s.User, s.Session.Id, r.Current, r.Next, ct);
            return Results.Ok(new { ok = true });
        });

        api.MapPost("/profile", async (ProfileChange r, HttpContext ctx, AccountStore store, CancellationToken ct) =>
        {
            if (await CurrentAsync(ctx, store, ct) is not { } s) return Results.Problem("Not signed in.", statusCode: 401);
            var name = AccountService.CheckedName(r.Name);
            await store.SetNameAsync(s.User.Id, name, ct);
            return Results.Ok(await MeAsync(store, (await store.ByIdAsync(s.User.Id, ct))!, ct));
        });

        api.MapGet("/sessions", async (HttpContext ctx, AccountStore store, CancellationToken ct) =>
        {
            if (await CurrentAsync(ctx, store, ct) is not { } s) return Results.Problem("Not signed in.", statusCode: 401);
            var list = await store.SessionsAsync(s.User.Id, ct);
            return Results.Ok(list.Select(x => new { x.Id, x.Kind, x.Device, x.Ip, x.Created, x.LastSeen, current = x.Id == s.Session.Id }));
        });

        api.MapPost("/sessions/{id:guid}/revoke", async (Guid id, HttpContext ctx, AccountStore store, CancellationToken ct) =>
        {
            if (await CurrentAsync(ctx, store, ct) is not { } s) return Results.Problem("Not signed in.", statusCode: 401);
            return await store.RevokeSessionAsync(s.User.Id, id, ct) ? Results.Ok(new { ok = true }) : Results.Problem("That session has already ended.", statusCode: 404);
        });

        api.MapPost("/providers/{provider}/unlink", async (string provider, HttpContext ctx, AccountStore store, CancellationToken ct) =>
        {
            if (await CurrentAsync(ctx, store, ct) is not { } s) return Results.Problem("Not signed in.", statusCode: 401);
            var linked = await store.ProvidersAsync(s.User.Id, ct);
            // Never leave an account without a way in.
            if (!s.User.HasPassword && linked.Count(l => l.Provider != provider) == 0)
                return Results.Problem("Set a password first, or this account would have no way to sign in.", statusCode: 409);
            return await store.UnlinkProviderAsync(s.User.Id, provider, ct) ? Results.Ok(new { ok = true }) : Results.Problem("Not connected.", statusCode: 404);
        });

        // GitHub / Google

        app.MapGet("/auth/{provider}", async (string provider, string? @return, bool? link, HttpContext ctx, OAuth oauth, AccountOptions options, AccountStore store, CancellationToken ct) =>
        {
            if (!oauth.Enabled(provider)) return Results.Redirect("/app#/sign-in?error=" + Uri.EscapeDataString("That sign-in option isn't set up yet."));
            Guid? linkTo = null;
            if (link == true)
            {
                if (await CurrentAsync(ctx, store, ct) is not { } s) return Results.Redirect("/app#/sign-in");
                linkTo = s.User.Id;
            }
            var (url, state) = oauth.Start(provider, options.BaseUrl(ctx.Request), SafeReturn(@return), linkTo);
            ctx.Response.Cookies.Append(OAuth.StateCookie, state, new CookieOptions
            {
                HttpOnly = true,
                Secure = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/auth",
                MaxAge = TimeSpan.FromMinutes(10),
            });
            return Results.Redirect(url);
        });

        app.MapGet("/auth/{provider}/callback", async (string provider, string? code, string? state, string? error, HttpContext ctx,
            OAuth oauth, AccountOptions options, AccountService accounts, AccountStore store, ILoggerFactory logs, CancellationToken ct) =>
        {
            var pending = oauth.Take(provider, state, ctx.Request.Cookies[OAuth.StateCookie]);
            ctx.Response.Cookies.Delete(OAuth.StateCookie, new CookieOptions { Path = "/auth", Secure = ctx.Request.IsHttps, HttpOnly = true, SameSite = SameSiteMode.Lax });
            static IResult Back(string message) => Results.Redirect("/app#/sign-in?error=" + Uri.EscapeDataString(message));
            if (pending is null) return Back("That sign-in took too long or came from somewhere else. Try again.");
            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return Back("Sign-in was cancelled.");
            ProviderProfile? profile;
            try
            {
                profile = await oauth.ProfileAsync(provider, code, pending.Verifier, options.BaseUrl(ctx.Request), ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                logs.CreateLogger("YHDE.Auth").LogWarning(e, "Sign-in with {Provider} failed", provider);
                profile = null;
            }
            if (profile is null) return Back($"{AccountService.Title(provider)} didn't let us in. Try again.");
            try
            {
                var user = await accounts.CompleteProviderAsync(profile, pending.LinkTo, ct);
                if (pending.LinkTo is null) await SignInAsync(ctx, store, user, ct);
                return Results.Redirect(pending.ReturnTo);
            }
            catch (AccountService.Refused r)
            {
                return Back(r.Message);
            }
        });
    }

    // Helpers

    private static CookieOptions CookieOptions(HttpContext ctx) => new()
    {
        HttpOnly = true,
        Secure = ctx.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = "/",
    };

    private static async Task SignInAsync(HttpContext ctx, AccountStore store, User user, CancellationToken ct)
    {
        // A new session each time: an old cookie (maybe planted) is never reused.
        if (ctx.Request.Cookies[SessionCookie] is { Length: > 0 } old) await store.RevokeTokenAsync(old, ct);
        var token = await store.CreateSessionAsync(user.Id, "web", Device(ctx), Ip(ctx), AccountStore.WebSessionLifetime, ct);
        var o = CookieOptions(ctx);
        o.MaxAge = AccountStore.WebSessionLifetime;
        ctx.Response.Cookies.Append(SessionCookie, token, o);
    }

    public static async Task<(UserSession Session, User User)?> CurrentAsync(HttpContext ctx, AccountStore store, CancellationToken ct) =>
        ctx.Request.Cookies[SessionCookie] is { Length: > 0 } token ? await store.SessionAsync(token, "web", ct) : null;

    private static async Task<object> MeAsync(AccountStore store, User u, CancellationToken ct) => new
    {
        id = u.Id,
        email = u.Email,
        name = u.Name,
        emailVerified = u.EmailVerified,
        hasPassword = u.HasPassword,
        created = u.Created,
        providers = (await store.ProvidersAsync(u.Id, ct)).Select(p => new { p.Provider, p.Email, p.Created }),
    };

    // Only a path on this site: one leading slash, and no backslash, space or
    // control character anywhere (browsers turn "/<tab>/evil.com" and
    // "/\evil.com" into another site).
    public static string SafeReturn(string? r) =>
        r is { Length: > 1 and < 300 } && r[0] == '/' && r[1] != '/'
        && r.All(c => c > ' ' && c < (char)0x7F && c != '\\') ? r : "/app#/projects";
}
