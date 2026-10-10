using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using YHDE.Server.Framing;
using YHDE.Server.Framing.Messages;
using YHDE.Server.Gateway;

namespace YHDE.Server.Presence;

// A session's current presence, held only in memory (presence.md).
public sealed record PresenceEntry(
    Guid SessionId,
    Guid ActorId,
    Guid BranchId,
    string DisplayName,
    string Scene,
    string Tool,
    string StateJson,
    DateTimeOffset UpdatedAt,
    Guid MemberId = default);

// Ephemeral presence relay (presence.md).
//
// - Last-write-wins, lossy, never persisted, never part of the operation log.
// - Scoped to the branch a session is subscribed to: peers only see presence
//   for the branch they follow (presence.md).
// - Every inbound update is sanitized and bounded; clients are untrusted
//   (security.md). Identity comes from the session, never from the payload.
// - Per-session token-bucket rate limit; excess updates are dropped, which is
//   correct for a lossy channel (presence.md).
public sealed class PresenceService(
    ISessionManager sessions,
    TimeProvider time,
    ILogger<PresenceService> logger)
{
    public const int MaxDisplayNameLength = 48;
    public const int MaxSceneLength = 512;
    public const int MaxToolLength = 64;
    public const int MaxStateBytes = 16 * 1024;
    public const double UpdatesPerSecond = 30;
    public const double Burst = 30;

    private readonly ConcurrentDictionary<Guid, PresenceEntry> _entries = new();
    private readonly ConcurrentDictionary<Guid, TokenBucket> _buckets = new();

    public IReadOnlyList<PresenceEntry> GetBranchEntries(Guid branchId)
        => _entries.Values.Where(e => e.BranchId == branchId).ToList();

    public PresenceEntry? Get(Guid sessionId)
        => _entries.TryGetValue(sessionId, out var e) ? e : null;

    // Apply an update from a subscribed session and fan it out to the other
    // subscribers of the same branch. Returns the stored entry, or null if the
    // update was dropped (not subscribed or rate limited).
    public async Task<PresenceEntry?> HandleUpdateAsync(
        SessionState session, PresenceUpdatePayload msg, CancellationToken ct)
    {
        if (session.SubscribedBranchId is not { } branchId)
            return null;

        var now = time.GetUtcNow();
        var bucket = _buckets.GetOrAdd(session.SessionId, _ => new TokenBucket(UpdatesPerSecond, Burst, now));
        if (!bucket.TryTake(now))
            return null;

        var entry = new PresenceEntry(
            session.SessionId,
            session.ActorId,
            branchId,
            SanitizeName(msg.DisplayName),
            SanitizeScene(msg.Scene),
            SanitizeText(msg.Tool, MaxToolLength),
            SanitizeState(msg.StateJson),
            now,
            session.MemberId);
        _entries[session.SessionId] = entry;

        // The same person's older entry whose connection is gone (a reconnect
        // before the server noticed): it goes now, not at the next sweep.
        var live = sessions.GetBranchSubscribers(branchId).Select(s => s.SessionId).ToHashSet();
        foreach (var stale in _entries.Values.Where(e => e.BranchId == branchId && e.MemberId == session.MemberId
                     && e.SessionId != session.SessionId && !live.Contains(e.SessionId)).ToList())
            await RemoveAsync(stale.SessionId, ct);

        var frame = BuildFrame(new PresenceStatePayload { Entries = [ToPayload(entry)], Full = false });
        await FanOutAsync(branchId, except: session.SessionId, frame, ct, droppable: true);
        return entry;
    }

    // Send the full presence snapshot of the session's branch (excluding itself).
    // Called when a session subscribes (presence.md "Join").
    public Task SendSnapshotAsync(SessionState session, CancellationToken ct)
    {
        if (session.SubscribedBranchId is not { } branchId)
            return Task.CompletedTask;

        var entries = GetBranchEntries(branchId)
            .Where(e => e.SessionId != session.SessionId)
            .Select(ToPayload)
            .ToArray();
        return sessions.SendFrameAsync(session, BuildFrame(new PresenceStatePayload { Entries = entries, Full = true }), ct);
    }

    // Remove a session's presence (disconnect or branch switch) and tell its
    // former peers it left (presence.md "Leave / disconnect").
    public async Task RemoveAsync(Guid sessionId, CancellationToken ct)
    {
        _buckets.TryRemove(sessionId, out _);
        if (!_entries.TryRemove(sessionId, out var entry))
            return;

        var frame = BuildFrame(new PresenceStatePayload { Left = [sessionId.ToByteArray()], Full = false });
        await FanOutAsync(entry.BranchId, except: sessionId, frame, ct);
        logger.LogDebug("Presence removed for session {SessionId}", sessionId);
    }

    // Queues only (SessionManager): a stalled peer never holds up the sender.
    // A cursor update may be skipped for a lagging peer; a leave never is.
    // Removes every entry whose session is not in `live` (SessionSweeper).
    public async Task RemoveAllExceptAsync(IReadOnlySet<Guid> live, CancellationToken ct)
    {
        foreach (var id in _entries.Keys.Where(id => !live.Contains(id)).ToList())
            await RemoveAsync(id, ct);
        foreach (var id in _buckets.Keys.Where(id => !live.Contains(id)).ToList())
            _buckets.TryRemove(id, out _);
    }

    private async Task FanOutAsync(Guid branchId, Guid except, Frame frame, CancellationToken ct, bool droppable = false)
    {
        var targets = sessions.GetBranchSubscribers(branchId).Where(s => s.SessionId != except).ToList();
        if (targets.Count == 0) return;
        var wire = Codec.Encode(frame);
        await Task.WhenAll(targets.Select(s => sessions.SendEncodedAsync(s, wire, ct, droppable)));
    }

    private static Frame BuildFrame(PresenceStatePayload payload) => new()
    {
        MsgType = MessageType.PresenceState,
        Channel = Channel.Presence,
        Payload = Codec.EncodePayload(payload),
    };

    public static PresenceEntryPayload ToPayload(PresenceEntry e) => new()
    {
        SessionId = e.SessionId.ToByteArray(),
        ActorId = e.ActorId.ToByteArray(),
        DisplayName = e.DisplayName,
        Scene = e.Scene,
        Tool = e.Tool,
        StateJson = e.StateJson,
        UpdatedAtUnixMs = e.UpdatedAt.ToUnixTimeMilliseconds(),
        MemberId = e.MemberId == Guid.Empty ? [] : e.MemberId.ToByteArray(),
    };

    // Sanitization (never trust clients)

    public static string SanitizeText(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var ch in value)
        {
            // Control characters and direction overrides (which can make
            // text read backwards) never pass.
            if (char.IsControl(ch) || ch is >= (char)0x202A and <= (char)0x202E or >= (char)0x2066 and <= (char)0x2069) continue;
            if (sb.Length >= maxLength) break;
            sb.Append(ch);
        }
        // Never leave half a surrogate pair at the cut.
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;
        return sb.ToString().Trim();
    }

    // Editors choose their own name: cleaned, and never an official-looking
    // one (Accounts/NameRules.cs).
    public static string SanitizeName(string? value) => Accounts.NameRules.EditorName(value);

    public static string SanitizeScene(string? value)
    {
        var scene = SanitizeText(value, MaxSceneLength);
        return scene.StartsWith("res://", StringComparison.Ordinal) ? scene : "";
    }

    public static string SanitizeState(string? json)
    {
        if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > MaxStateBytes)
            return "{}";
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            return doc.RootElement.ValueKind == JsonValueKind.Object ? json : "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    // Classic token bucket; one per session. Not thread-safe across sessions
    // but each session's updates arrive on its own sequential read loop.
    private sealed class TokenBucket(double ratePerSecond, double capacity, DateTimeOffset now)
    {
        private readonly double _capacity = capacity;
        private double _tokens = capacity;
        private DateTimeOffset _last = now;

        public bool TryTake(DateTimeOffset at)
        {
            var elapsed = (at - _last).TotalSeconds;
            if (elapsed > 0)
            {
                _tokens = Math.Min(_capacity, _tokens + elapsed * ratePerSecond);
                _last = at;
            }
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}
