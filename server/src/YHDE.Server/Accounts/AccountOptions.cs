using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;

namespace YHDE.Server.Accounts;

// Settings (appsettings or environment, e.g. Yhde__PublicUrl):
//   Yhde:PublicUrl                 https://yhde.frostinteractive.fi (links in emails, OAuth return address)
//   Yhde:Auth:GitHub:ClientId / ClientSecret
//   Yhde:Auth:Google:ClientId / ClientSecret
//   Yhde:Smtp:Host / Port / User / Password / From
// A provider without both values is simply not offered.
public sealed class AccountOptions
{
    public string PublicUrl { get; init; } = "";
    public (string Id, string Secret)? GitHub { get; init; }
    public (string Id, string Secret)? Google { get; init; }

    public static AccountOptions From(IConfiguration c)
    {
        static (string, string)? Pair(IConfiguration c, string name)
        {
            var id = c[$"Yhde:Auth:{name}:ClientId"]?.Trim();
            var secret = c[$"Yhde:Auth:{name}:ClientSecret"]?.Trim();
            return string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret) ? null : (id, secret);
        }
        return new AccountOptions
        {
            PublicUrl = (c["Yhde:PublicUrl"] ?? "").Trim().TrimEnd('/'),
            GitHub = Pair(c, "GitHub"),
            Google = Pair(c, "Google"),
        };
    }

    // The address people reach the site on: the configured one, or the one
    // this request came in on (development, and before a domain is set).
    public string BaseUrl(HttpRequest request) =>
        PublicUrl.Length > 0 ? PublicUrl : $"{request.Scheme}://{request.Host}";
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string text, CancellationToken ct);
}

// Sends through any SMTP service (a free tier is enough).
public sealed class SmtpEmailSender(IConfiguration c, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string text, CancellationToken ct)
    {
        using var client = new SmtpClient(c["Yhde:Smtp:Host"], int.TryParse(c["Yhde:Smtp:Port"], out var port) ? port : 587)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(c["Yhde:Smtp:User"], c["Yhde:Smtp:Password"]),
        };
        using var message = new MailMessage(c["Yhde:Smtp:From"] ?? "", to, subject, text);
        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (SmtpException e)
        {
            logger.LogError(e, "Email to {Domain} not sent", to.Split('@').LastOrDefault());
            throw;
        }
    }
}

// No SMTP configured: the message goes to the server log so the operator can
// still verify the first accounts. Never used when SMTP is set.
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string text, CancellationToken ct)
    {
        logger.LogWarning("No email service is set up (Yhde:Smtp). Email to {To}: {Subject}\n{Text}", to, subject, text);
        return Task.CompletedTask;
    }
}

// Counts attempts per key (an address, an email) in a time window.
//
// Old entries are swept out only once their window has passed, never all at
// once: clearing everything when the table got big let anyone reset every
// limit (the per-account password limit too) by trying many random keys.
// If the table is still full of live entries, new keys are refused (fail
// closed) until the sweep frees room.
public sealed class Limiter(int max, TimeSpan window, int maxKeys = 200_000)
{
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Since)> _tries = new();
    private long _nextSweepTicks;

    // True while the key is under the limit; counts this try.
    public bool Try(string key)
    {
        var now = DateTimeOffset.UtcNow;
        if (_tries.Count >= maxKeys / 2) Sweep(now);
        if (_tries.Count >= maxKeys && !_tries.ContainsKey(key)) return false;
        var e = _tries.AddOrUpdate(key, _ => (1, now), (_, old) => now - old.Since >= window ? (1, now) : (old.Count + 1, old.Since));
        return e.Count <= max;
    }

    // Whether the key is over the limit, without counting a try.
    public bool Blocked(string key) =>
        _tries.TryGetValue(key, out var e) && DateTimeOffset.UtcNow - e.Since < window && e.Count >= max;

    private void Sweep(DateTimeOffset now)
    {
        // At most once a second, however busy.
        var next = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < next || Interlocked.CompareExchange(ref _nextSweepTicks, now.UtcTicks + TimeSpan.TicksPerSecond, next) != next) return;
        foreach (var (k, v) in _tries)
            if (now - v.Since >= window) _tries.TryRemove(k, out _);
    }

    public void Forget(string key) => _tries.TryRemove(key, out _);
}
