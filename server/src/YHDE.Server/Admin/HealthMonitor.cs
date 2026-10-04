using System.Diagnostics;
using YHDE.Server.Gateway;

namespace YHDE.Server.Admin;

// The server's own health for the admin page: one sample a minute for the
// last 24 hours (connections, memory, CPU, requests, errors), kept in memory.
// Also keeps the connection history's "last seen" times fresh.
public sealed class HealthMonitor(ISessionManager sessions, SessionLog log, ILogger<HealthMonitor> logger) : BackgroundService
{
    public sealed record Sample(DateTimeOffset At, int Connections, int Editing, double MemoryMb, double CpuPercent, long Requests, long Errors);

    private const int Keep = 24 * 60;
    private readonly object _lock = new();
    private readonly Queue<Sample> _samples = new();
    private long _requests;
    private long _errors;
    private long _requestsTotal;
    private long _errorsTotal;
    private TimeSpan _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
    private DateTimeOffset _lastAt = DateTimeOffset.UtcNow;

    public static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;

    public long RequestsTotal => Interlocked.Read(ref _requestsTotal);
    public long ErrorsTotal => Interlocked.Read(ref _errorsTotal);

    public void CountRequest(bool failed)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Increment(ref _requestsTotal);
        if (!failed) return;
        Interlocked.Increment(ref _errors);
        Interlocked.Increment(ref _errorsTotal);
    }

    public IReadOnlyList<Sample> Samples()
    {
        lock (_lock) return [.. _samples];
    }

    public Sample Now() => Take(false);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await log.CloseLeftoversAsync();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        Take(true);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                Take(true);
                await log.TouchAsync(sessions.All.Where(s => s.IsSubscribed).Select(s => s.SessionId).ToArray());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Health sample failed");
            }
        }
    }

    private Sample Take(bool keep)
    {
        var process = Process.GetCurrentProcess();
        var now = DateTimeOffset.UtcNow;
        double cpu;
        lock (_lock)
        {
            var used = process.TotalProcessorTime - _lastCpu;
            var wall = now - _lastAt;
            cpu = wall.TotalMilliseconds > 0 ? used.TotalMilliseconds / wall.TotalMilliseconds / Environment.ProcessorCount * 100 : 0;
            if (keep)
            {
                _lastCpu = process.TotalProcessorTime;
                _lastAt = now;
            }
        }
        var all = sessions.All;
        var sample = new Sample(now, all.Count, all.Count(s => s.IsSubscribed), process.WorkingSet64 / 1048576.0,
            Math.Round(Math.Clamp(cpu, 0, 100), 1),
            keep ? Interlocked.Exchange(ref _requests, 0) : Interlocked.Read(ref _requests),
            keep ? Interlocked.Exchange(ref _errors, 0) : Interlocked.Read(ref _errors));
        if (!keep) return sample;
        lock (_lock)
        {
            _samples.Enqueue(sample);
            while (_samples.Count > Keep) _samples.Dequeue();
        }
        return sample;
    }
}
