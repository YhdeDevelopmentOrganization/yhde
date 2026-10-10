using Dapper;
using YHDE.Server.Persistence;

namespace YHDE.Server.Assets;

// Hourly clean-up of the blob store (assets.md): unfinished uploads nobody
// continued for a week, and uploads no operation used within two days. Such a
// file's bytes go only when no project holds it, no news picture uses it, and
// it was not touched within a day.
public sealed class BlobJanitor(BlobStore blobs, IProjectBlobs projectBlobs, Database db, ILogger<BlobJanitor> logger) : BackgroundService
{
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromDays(2);
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Blob clean-up failed; trying again in an hour");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        blobs.RemoveStaleParts();
        var gone = await projectBlobs.ExpirePendingAsync(PendingLifetime, ct);
        if (gone.Count == 0) return 0;
        await using var conn = await db.OpenAsync(ct);
        var pictures = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT hash FROM site_media WHERE hash = ANY(@gone)", new { gone = gone.ToArray() }, cancellationToken: ct)))
            .ToHashSet(StringComparer.Ordinal);
        var removed = gone.Count(h => !pictures.Contains(h) && blobs.RemoveIfStale(h));
        if (removed > 0) logger.LogInformation("Removed {Count} uploaded files no operation used", removed);
        return removed;
    }
}
