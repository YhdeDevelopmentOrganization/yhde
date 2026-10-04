using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace YHDE.Server.Accounts;

// Who a provider says the person is. Email is null unless the provider says
// it is verified: an unverified address is never used to find an account.
public sealed record ProviderProfile(string Provider, string Id, string? VerifiedEmail, string Name);

// GitHub and Google sign-in: the authorization code flow with PKCE and a
// one-time state bound to the browser by a cookie (ADR 0015). Provider secrets
// stay on the server.
public sealed class OAuth(AccountOptions options, IHttpClientFactory http)
{
    public const string StateCookie = "yhde_oauth";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public sealed record Pending(string Provider, string Verifier, string ReturnTo, Guid? LinkTo, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    public bool Enabled(string provider) => Client(provider) is not null;

    private (string Id, string Secret)? Client(string provider) => provider switch
    {
        "github" => options.GitHub,
        "google" => options.Google,
        _ => null,
    };

    public static string RedirectUri(string baseUrl, string provider) => $"{baseUrl}/auth/{provider}/callback";

    // Where to send the browser, and the state value for the cookie.
    public (string Url, string State) Start(string provider, string baseUrl, string returnTo, Guid? linkTo)
    {
        var (id, _) = Client(provider) ?? throw new InvalidOperationException("Provider not set up");
        foreach (var (k, v) in _pending) if (v.Expires < DateTimeOffset.UtcNow) _pending.TryRemove(k, out _);
        var state = Secrets.NewToken();
        var verifier = Secrets.NewToken();
        _pending[state] = new Pending(provider, verifier, returnTo, linkTo, DateTimeOffset.UtcNow.Add(StateLifetime));
        var redirect = Uri.EscapeDataString(RedirectUri(baseUrl, provider));
        var challenge = Secrets.PkceChallenge(verifier);
        var url = provider switch
        {
            "github" => $"https://github.com/login/oauth/authorize?client_id={Uri.EscapeDataString(id)}&redirect_uri={redirect}" +
                        $"&scope={Uri.EscapeDataString("read:user user:email")}&state={state}&code_challenge={challenge}&code_challenge_method=S256&allow_signup=true",
            _ => $"https://accounts.google.com/o/oauth2/v2/auth?response_type=code&client_id={Uri.EscapeDataString(id)}&redirect_uri={redirect}" +
                 $"&scope={Uri.EscapeDataString("openid email profile")}&state={state}&code_challenge={challenge}&code_challenge_method=S256&prompt=select_account",
        };
        return (url, state);
    }

    // Takes the state once: it must match the cookie and not be expired.
    public Pending? Take(string provider, string? state, string? cookie)
    {
        if (string.IsNullOrEmpty(state) || state != cookie || !_pending.TryRemove(state, out var p)) return null;
        return p.Provider == provider && p.Expires > DateTimeOffset.UtcNow ? p : null;
    }

    public async Task<ProviderProfile?> ProfileAsync(string provider, string code, string verifier, string baseUrl, CancellationToken ct)
    {
        var (id, secret) = Client(provider) ?? throw new InvalidOperationException("Provider not set up");
        var client = http.CreateClient("oauth");
        var form = new Dictionary<string, string>
        {
            ["client_id"] = id,
            ["client_secret"] = secret,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri(baseUrl, provider),
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
        };
        var tokenUrl = provider == "github" ? "https://github.com/login/oauth/access_token" : "https://oauth2.googleapis.com/token";
        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await client.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        using var tokenDoc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (!tokenDoc.RootElement.TryGetProperty("access_token", out var at) || at.GetString() is not { Length: > 0 } accessToken) return null;

        return provider == "github" ? await GitHubAsync(client, accessToken, ct) : await GoogleAsync(client, accessToken, ct);
    }

    private static async Task<JsonDocument?> GetAsync(HttpClient client, string url, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.UserAgent.ParseAdd("YHDE");
        using var res = await client.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)) : null;
    }

    private static async Task<ProviderProfile?> GitHubAsync(HttpClient client, string token, CancellationToken ct)
    {
        using var user = await GetAsync(client, "https://api.github.com/user", token, ct);
        if (user is null || !user.RootElement.TryGetProperty("id", out var idProp)) return null;
        var name = Str(user.RootElement, "name") is { Length: > 0 } n ? n : Str(user.RootElement, "login") ?? "";
        string? email = null;
        using var emails = await GetAsync(client, "https://api.github.com/user/emails", token, ct);
        if (emails is { RootElement.ValueKind: JsonValueKind.Array })
            foreach (var e in emails.RootElement.EnumerateArray())
                if (e.TryGetProperty("primary", out var p) && p.GetBoolean() && e.TryGetProperty("verified", out var v) && v.GetBoolean())
                    email = Str(e, "email");
        return new ProviderProfile("github", idProp.GetRawText(), email, name);
    }

    private static async Task<ProviderProfile?> GoogleAsync(HttpClient client, string token, CancellationToken ct)
    {
        using var info = await GetAsync(client, "https://openidconnect.googleapis.com/v1/userinfo", token, ct);
        if (info is null || Str(info.RootElement, "sub") is not { Length: > 0 } sub) return null;
        var verified = info.RootElement.TryGetProperty("email_verified", out var ev) && ev.ValueKind == JsonValueKind.True;
        return new ProviderProfile("google", sub, verified ? Str(info.RootElement, "email") : null, Str(info.RootElement, "name") ?? "");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
