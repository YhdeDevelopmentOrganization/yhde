using System.Collections.Concurrent;
using System.Text.Json;
using YHDE.Server.Domain;

namespace YHDE.Server.Teams;

// Keeps an owner's projects inside their plan's storage (teams.md §3): a new
// or changed file that would take them over it is refused, and Godot shows
// why. The Godot panel warns from 80 % on, before this happens. A changed
// file is refused only once the owner is already over (its old size isn't
// known here); projects made on the admin page (no owner) have no limit.
public sealed class StorageQuota(TeamStore teams)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<Guid, (Guid? TeamId, long Used, long Limit, DateTimeOffset Until)> _cache = new();

    // Null when the change may go in; else why not, in words.
    public async Task<string?> RefuseAsync(Guid projectId, string type, string payload, CancellationToken ct)
    {
        if (type is not (OperationType.RegisterAsset or OperationType.UpdateAsset)) return null;
        long size;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            size = doc.RootElement.TryGetProperty("n", out var n) && n.TryGetInt64(out var v) ? v : 0;
        }
        catch (JsonException)
        {
            return null; // the processor refuses it for being malformed
        }
        if (size <= 0) return null;

        var now = DateTimeOffset.UtcNow;
        if (!_cache.TryGetValue(projectId, out var q) || q.Until <= now)
        {
            var team = await teams.OfProjectAsync(projectId, ct);
            var used = team is null ? 0 : (await teams.StatsAsync(await teams.ProjectIdsAsync(team.Id, ct), ct)).Sum(s => s.FileBytes);
            q = (team?.Id, used, team?.StorageBytes ?? long.MaxValue, now + CacheFor);
            if (_cache.Count > 10_000) _cache.Clear();
        }
        if (q.TeamId is null) return null;
        var added = type == OperationType.RegisterAsset ? size : 0;
        if (q.Used + added > q.Limit || q.Used >= q.Limit)
        {
            _cache[projectId] = q;
            return "The project's owner has run out of storage, so this file wasn't added. Free space by deleting files or a project on the YHDE website.";
        }
        _cache[projectId] = q with { Used = q.Used + added };
        return null;
    }
}
