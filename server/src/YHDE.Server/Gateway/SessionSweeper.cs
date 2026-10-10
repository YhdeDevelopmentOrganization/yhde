using YHDE.Server.Presence;

namespace YHDE.Server.Gateway;

// Closes connections that went silent (reliability.md). The editor pings
// every 5 seconds, so a connection with nothing from it for IdleTimeout is
// half-open (a laptop that lost Wi-Fi): until it is closed, its person stays
// in the project with a frozen cursor. Also removes presence entries whose
// connection is gone, in case a clean-up was missed.
public sealed class SessionSweeper(ISessionManager sessions, PresenceService presence, IConfiguration configuration,
    ILogger<SessionSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    public TimeSpan IdleTimeout { get; } = TimeSpan.FromSeconds(Math.Max(15, configuration.GetValue("Yhde:IdleTimeoutSeconds", 60)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(Environment.TickCount64, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Session sweep failed");
            }
        }
    }

    public async Task<int> SweepAsync(long nowTicks, CancellationToken ct)
    {
        var closed = 0;
        foreach (var s in sessions.All)
        {
            if (nowTicks - s.LastReceivedTicks <= (long)IdleTimeout.TotalMilliseconds) continue;
            sessions.Disconnect(s, $"nothing received for {IdleTimeout.TotalSeconds:0} s");
            closed++;
        }
        var live = sessions.All.Select(s => s.SessionId).ToHashSet();
        await presence.RemoveAllExceptAsync(live, ct);
        return closed;
    }
}
