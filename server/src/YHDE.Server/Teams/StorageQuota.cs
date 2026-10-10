using System.Text.Json;
using YHDE.Server.Assets;
using YHDE.Server.Domain;

namespace YHDE.Server.Teams;

// Keeps an owner's projects inside their plan's storage (teams.md §3). Bytes
// count from the moment they are uploaded (assets.md): an upload that would
// take the owner over is refused before it starts, and a new file in the log
// counts unless its upload was already counted. The Godot panel warns from
// 80 % on, before this happens. A changed file is refused only once the owner
// is already over (its old size isn't known here); projects made on the admin
// page (no owner) have no limit.
//
// What is in use is read from the database at most every 15 seconds; in
// between, every charge is added under one lock, so two uploads at the same
// moment cannot both take the last free bytes.
public sealed class StorageQuota(TeamStore teams, IProjectBlobs? projectBlobs = null)
{
    public const string FullMessage =
        "The project's owner has run out of storage, so this file wasn't added. Free space by deleting files or a project on the YHDE website.";

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, (Guid? TeamId, DateTimeOffset Until)> _teamOf = new();
    private readonly Dictionary<Guid, (long Used, long Limit, DateTimeOffset Until)> _usage = new();

    // Null when the change may go in; else why not, in words. `uploadCounted`:
    // the file's bytes were uploaded to this project and already counted.
    public async Task<string?> RefuseAsync(Guid projectId, string type, string payload, CancellationToken ct, bool uploadCounted = false)
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
        var added = type == OperationType.RegisterAsset && !uploadCounted ? size : 0;
        return await ChargeAsync(projectId, added, ct, counted: uploadCounted) ? null : FullMessage;
    }

    // Null when `bytes` more may be uploaded to the project; else why not.
    public async Task<string?> RefuseUploadAsync(Guid projectId, long bytes, CancellationToken ct) =>
        await ChargeAsync(projectId, Math.Max(0, bytes), ct) ? null : FullMessage;

    // `counted`: the bytes were counted at upload, so being exactly full is fine.
    private async Task<bool> ChargeAsync(Guid projectId, long added, CancellationToken ct, bool counted = false)
    {
        var now = DateTimeOffset.UtcNow;
        Guid? teamId;
        bool knownTeam;
        lock (_gate)
        {
            knownTeam = _teamOf.TryGetValue(projectId, out var t) && t.Until > now;
            teamId = t.TeamId;
        }
        Team? team = null;
        if (!knownTeam)
        {
            team = await teams.OfProjectAsync(projectId, ct);
            teamId = team?.Id;
            lock (_gate)
            {
                if (_teamOf.Count > 10_000) _teamOf.Clear();
                _teamOf[projectId] = (teamId, now + CacheFor);
            }
        }
        if (teamId is not { } tid) return true;

        bool fresh;
        lock (_gate) fresh = _usage.TryGetValue(tid, out var u) && u.Until > now;
        if (!fresh)
        {
            team ??= await teams.OfProjectAsync(projectId, ct);
            if (team is null) return true;
            var ids = await teams.ProjectIdsAsync(tid, ct);
            var used = (await teams.StatsAsync(ids, ct)).Sum(s => s.FileBytes);
            if (projectBlobs is not null) used += await projectBlobs.PendingBytesAsync(ids, ct);
            lock (_gate)
            {
                // Another request may have refreshed it meanwhile: keep its charges.
                if (!(_usage.TryGetValue(tid, out var u) && u.Until > now))
                {
                    if (_usage.Count > 10_000) _usage.Clear();
                    _usage[tid] = (used, team.StorageBytes, now + CacheFor);
                }
            }
        }
        lock (_gate)
        {
            if (!_usage.TryGetValue(tid, out var q)) return true;
            if (q.Used + added > q.Limit || (!counted && q.Used >= q.Limit)) return false;
            _usage[tid] = q with { Used = q.Used + added };
            return true;
        }
    }
}
