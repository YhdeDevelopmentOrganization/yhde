using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YHDE.Server.Framing;
using YHDE.Server.Framing.Messages;
using YHDE.Server.Gateway;

namespace YHDE.Server.Social;

// Chat and Figma-style comments (social.md).
//
// - Chat: one channel per project plus direct messages between two members.
//   Project messages go to everyone connected to the project; a direct
//   message only to the sessions of its two members.
// - Comments: threads pinned in a scene (to a node by its YHDE id, or to a
//   spot), with replies, resolve/reopen, edit and remove. Scoped to a branch.
// - Everything is stored so people who were away see it, but none of it is
//   project state: it never enters the operation log and undo never touches it.
// - Clients are untrusted: every field is validated and bounded, identity comes
//   from the session, only authors edit or remove their own comments, and each
//   session is rate limited.
public sealed class SocialService(
    ISessionManager sessions,
    ISocialStore store,
    TimeProvider time,
    ILogger<SocialService> logger)
{
    public const int MaxBodyChars = 4000;
    public const int MaxPathChars = 512;
    public const int RecentChat = 100;
    public const int HistoryPage = 50;
    public const int MaxThreads = 500;
    public static readonly TimeSpan ResolvedKept = TimeSpan.FromDays(30);
    public const double RequestsPerSecond = 5;
    public const double Burst = 20;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };
    private readonly ConcurrentDictionary<Guid, TokenBucket> _buckets = new();

    // Entry points

    public async Task HandleAsync(SessionState session, SocialRequestPayload request, CancellationToken ct)
    {
        if (session.SubscribedProjectId is null || session.SubscribedBranchId is null)
        {
            await ErrorAsync(session, request, "Connect to a project first.", ct);
            return;
        }
        var now = time.GetUtcNow();
        var bucket = _buckets.GetOrAdd(session.SessionId, _ => new TokenBucket(RequestsPerSecond, Burst, now));
        if (!bucket.TryTake(now))
        {
            await ErrorAsync(session, request, "Slow down a little: too many messages at once.", ct);
            return;
        }

        JsonElement body;
        try
        {
            using var doc = JsonDocument.Parse(request.BodyJson, new JsonDocumentOptions { MaxDepth = 8 });
            body = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            await ErrorAsync(session, request, "Unreadable request.", ct);
            return;
        }
        if (body.ValueKind != JsonValueKind.Object)
        {
            await ErrorAsync(session, request, "Unreadable request.", ct);
            return;
        }

        try
        {
            var problem = request.Kind switch
            {
                "chat.send" => await ChatSendAsync(session, request, body, now, ct),
                "chat.history" => await ChatHistoryAsync(session, request, body, ct),
                "comment.create" => await CommentCreateAsync(session, request, body, now, ct),
                "comment.reply" => await CommentReplyAsync(session, request, body, now, ct),
                "comment.resolve" => await CommentResolveAsync(session, request, body, now, ct),
                "comment.edit" => await CommentEditAsync(session, request, body, now, ct),
                "comment.delete" => await CommentDeleteAsync(session, request, body, now, ct),
                _ => $"Unknown request '{Sanitize(request.Kind, 64)}'.",
            };
            if (problem is not null) await ErrorAsync(session, request, problem, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Social request {Kind} from session {SessionId} failed", request.Kind, session.SessionId);
            await ErrorAsync(session, request, "The server could not save that. Try again.", ct);
        }
    }

    // What a session needs on joining: recent chat and the branch's comments.
    public async Task SendInitialAsync(SessionState session, CancellationToken ct)
    {
        if (session.SubscribedProjectId is not { } projectId || session.SubscribedBranchId is not { } branchId) return;
        try
        {
            var channel = await store.ChatPageAsync(projectId, session.MemberId, null, null, RecentChat, ct);
            var direct = await store.RecentDirectAsync(projectId, session.MemberId, RecentChat, ct);
            var messages = channel.Concat(direct).OrderBy(m => m.CreatedAt).Select(ChatJson).ToList();
            await SendAsync(session, "chat.recent", new { messages, more = channel.Count >= RecentChat }, null, ct);

            var threads = await store.ThreadsAsync(branchId, time.GetUtcNow() - ResolvedKept, MaxThreads, ct);
            await SendAsync(session, "comment.threads", new { threads = threads.Select(ThreadJson).ToList() }, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not load chat and comments for session {SessionId}", session.SessionId);
            await SendAsync(session, "error", new { message = "Chat and comments could not be loaded." }, null, ct);
        }
    }

    public void Forget(Guid sessionId) => _buckets.TryRemove(sessionId, out _);

    // Chat

    private async Task<string?> ChatSendAsync(SessionState s, SocialRequestPayload req, JsonElement body, DateTimeOffset now, CancellationToken ct)
    {
        var text = Body(body);
        if (text is null) return "A message needs some text (at most 4000 characters).";
        Guid? to = null;
        if (body.TryGetProperty("to", out var toProp) && toProp.ValueKind != JsonValueKind.Null)
        {
            if (!TryGuid(toProp, out var recipient) || recipient == Guid.Empty || recipient == s.MemberId)
                return "That person cannot be messaged.";
            to = recipient;
        }
        var message = new ChatMessage(Guid.NewGuid(), s.SubscribedProjectId!.Value, s.MemberId, s.MemberName, to, text, now);
        await store.AddChatAsync(message, ct);

        var targets = sessions.GetProjectSessions(message.ProjectId)
            .Where(t => to is null || t.MemberId == s.MemberId || t.MemberId == to);
        await FanOutAsync(targets, s, req, "chat.message", ChatJson(message), ct);
        return null;
    }

    private async Task<string?> ChatHistoryAsync(SessionState s, SocialRequestPayload req, JsonElement body, CancellationToken ct)
    {
        Guid? with = null;
        if (body.TryGetProperty("with", out var w) && w.ValueKind != JsonValueKind.Null)
        {
            if (!TryGuid(w, out var id)) return "Unknown conversation.";
            with = id;
        }
        DateTimeOffset? before = null;
        if (body.TryGetProperty("before", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out var ms))
            before = DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ms, 0, 253402300799000));
        var page = await store.ChatPageAsync(s.SubscribedProjectId!.Value, s.MemberId, with, before, HistoryPage, ct);
        await SendAsync(s, "chat.history", new
        {
            with,
            messages = page.OrderBy(m => m.CreatedAt).Select(ChatJson).ToList(),
            more = page.Count >= HistoryPage,
        }, req.RequestId, ct);
        return null;
    }

    // Comments

    private async Task<string?> CommentCreateAsync(SessionState s, SocialRequestPayload req, JsonElement body, DateTimeOffset now, CancellationToken ct)
    {
        var text = Body(body);
        if (text is null) return "A comment needs some text (at most 4000 characters).";
        var scene = Sanitize(StringProp(body, "scene"), MaxPathChars);
        if (!scene.StartsWith("res://", StringComparison.Ordinal) || scene.Contains(".."))
            return "Comments go in a saved scene.";
        Guid? node = null;
        if (body.TryGetProperty("node", out var n) && n.ValueKind != JsonValueKind.Null)
        {
            if (!TryGuid(n, out var id)) return "Unknown node.";
            node = id;
        }
        var anchor = Anchor(body);
        if (anchor is null) return "A comment needs a position in the scene.";

        var thread = new CommentThread(Guid.NewGuid(), s.SubscribedProjectId!.Value, s.SubscribedBranchId!.Value, scene, node,
            Sanitize(StringProp(body, "path"), MaxPathChars), anchor, s.MemberId, s.MemberName, now, null, null, []);
        var first = new CommentMessage(Guid.NewGuid(), thread.ThreadId, s.MemberId, s.MemberName, text, now);
        await store.CreateThreadAsync(thread, first, ct);
        await BroadcastThreadAsync(s, req, thread.ThreadId, ct);
        return null;
    }

    private async Task<string?> CommentReplyAsync(SessionState s, SocialRequestPayload req, JsonElement body, DateTimeOffset now, CancellationToken ct)
    {
        var text = Body(body);
        if (text is null) return "A reply needs some text (at most 4000 characters).";
        var thread = await ThreadOf(s, body, "thread", ct);
        if (thread is null) return "That comment no longer exists.";
        await store.AddCommentAsync(new CommentMessage(Guid.NewGuid(), thread.ThreadId, s.MemberId, s.MemberName, text, now), ct);
        // Replying to a resolved thread reopens it, as in Figma.
        if (thread.ResolvedAt is not null) await store.SetResolvedAsync(thread.ThreadId, null, null, ct);
        await BroadcastThreadAsync(s, req, thread.ThreadId, ct);
        return null;
    }

    private async Task<string?> CommentResolveAsync(SessionState s, SocialRequestPayload req, JsonElement body, DateTimeOffset now, CancellationToken ct)
    {
        var thread = await ThreadOf(s, body, "thread", ct);
        if (thread is null) return "That comment no longer exists.";
        var resolved = !body.TryGetProperty("resolved", out var r) || r.ValueKind != JsonValueKind.False;
        await store.SetResolvedAsync(thread.ThreadId, resolved ? now : null, resolved ? s.MemberName : null, ct);
        await BroadcastThreadAsync(s, req, thread.ThreadId, ct);
        return null;
    }

    private async Task<string?> CommentEditAsync(SessionState s, SocialRequestPayload req, JsonElement body, DateTimeOffset now, CancellationToken ct)
    {
        var text = Body(body);
        if (text is null) return "A comment needs some text (at most 4000 characters).";
        var (message, problem) = await OwnMessage(s, body, ct);
        if (message is null) return problem;
        await store.EditCommentAsync(message, text, now, s.SubscribedProjectId!.Value, ct);
        await BroadcastThreadAsync(s, req, message.ThreadId, ct);
        return null;
    }

    private async Task<string?> CommentDeleteAsync(SessionState s, SocialRequestPayload req, JsonElement body, DateTimeOffset now, CancellationToken ct)
    {
        var (message, problem) = await OwnMessage(s, body, ct);
        if (message is null) return problem;
        await store.DeleteCommentAsync(message, now, s.SubscribedProjectId!.Value, ct);
        await BroadcastThreadAsync(s, req, message.ThreadId, ct);
        return null;
    }

    private async Task<(CommentMessage?, string?)> OwnMessage(SessionState s, JsonElement body, CancellationToken ct)
    {
        if (!body.TryGetProperty("message", out var m) || !TryGuid(m, out var id)) return (null, "Unknown comment.");
        var message = await store.GetCommentAsync(id, ct);
        if (message is null || message.DeletedAt is not null) return (null, "That comment no longer exists.");
        var thread = await store.GetThreadAsync(message.ThreadId, ct);
        if (thread is null || thread.BranchId != s.SubscribedBranchId) return (null, "That comment no longer exists.");
        if (message.AuthorId != s.MemberId) return (null, "Only its author can change a comment.");
        return (message, null);
    }

    private async Task<CommentThread?> ThreadOf(SessionState s, JsonElement body, string field, CancellationToken ct)
    {
        if (!body.TryGetProperty(field, out var t) || !TryGuid(t, out var id)) return null;
        var thread = await store.GetThreadAsync(id, ct);
        return thread is not null && thread.BranchId == s.SubscribedBranchId ? thread : null;
    }

    private async Task BroadcastThreadAsync(SessionState s, SocialRequestPayload req, Guid threadId, CancellationToken ct)
    {
        var thread = await store.GetThreadAsync(threadId, ct);
        if (thread is null) return;
        await FanOutAsync(sessions.GetBranchSubscribers(thread.BranchId), s, req, "comment.thread", ThreadJson(thread), ct);
    }

    // JSON shapes sent to editors

    private static object ChatJson(ChatMessage m) => new
    {
        id = m.MessageId,
        from = new { id = m.AuthorId, name = m.AuthorName },
        to = m.RecipientId,
        body = m.Body,
        at = m.CreatedAt.ToUnixTimeMilliseconds(),
    };

    private static object ThreadJson(CommentThread t) => new
    {
        id = t.ThreadId,
        scene = t.Scene,
        node = t.NodeId,
        path = t.NodePath,
        anchor = JsonNode.Parse(t.AnchorJson),
        author = new { id = t.AuthorId, name = t.AuthorName },
        created = t.CreatedAt.ToUnixTimeMilliseconds(),
        resolved = t.ResolvedAt is { } at ? new { by = t.ResolvedByName ?? "", at = at.ToUnixTimeMilliseconds() } : null,
        messages = t.Messages.Where(m => m.DeletedAt is null).Select(m => new
        {
            id = m.MessageId,
            author = new { id = m.AuthorId, name = m.AuthorName },
            body = m.Body,
            at = m.CreatedAt.ToUnixTimeMilliseconds(),
            edited = m.EditedAt is not null,
        }).ToList(),
    };

    // Validation

    // Text people wrote: newlines and tabs are kept, other control characters dropped.
    private static string? Body(JsonElement body)
    {
        var raw = StringProp(body, "body");
        if (raw.Length > MaxBodyChars * 2) return null;
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsControl(ch) && ch != '\n' && ch != '\t') continue;
            sb.Append(ch);
        }
        var text = sb.ToString().Trim();
        return text.Length is 0 or > MaxBodyChars ? null : text;
    }

    // {"space":"2d","x":…,"y":…} or {"space":"3d","x":…,"y":…,"z":…}: a point in
    // the node's local space (or the scene's, without a node). Rebuilt, never echoed.
    private static string? Anchor(JsonElement body)
    {
        if (!body.TryGetProperty("anchor", out var a) || a.ValueKind != JsonValueKind.Object) return null;
        var space = StringProp(a, "space");
        if (space is not ("2d" or "3d")) return null;
        double Coord(string name) =>
            a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            && double.IsFinite(d) && Math.Abs(d) <= 1e9 ? d : double.NaN;
        var x = Coord("x");
        var y = Coord("y");
        var z = space == "3d" ? Coord("z") : 0;
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z)) return null;
        return space == "3d"
            ? JsonSerializer.Serialize(new { space, x, y, z })
            : JsonSerializer.Serialize(new { space, x, y });
    }

    private static string StringProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool TryGuid(JsonElement e, out Guid id)
    {
        id = Guid.Empty;
        return e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out id);
    }

    private static string Sanitize(string? value, int max) =>
        Presence.PresenceService.SanitizeText(value, max);

    // Sending

    private async Task FanOutAsync(IEnumerable<SessionState> targets, SessionState origin, SocialRequestPayload req,
        string kind, object body, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        var plain = Build(kind, bytes, []);
        Frame? echo = null;
        var tasks = new List<Task>();
        foreach (var t in targets.DistinctBy(t => t.SessionId))
        {
            if (t.SessionId == origin.SessionId)
            {
                echo ??= Build(kind, bytes, req.RequestId);
                tasks.Add(sessions.SendFrameAsync(t, echo, ct));
            }
            else
            {
                tasks.Add(sessions.SendFrameAsync(t, plain, ct));
            }
        }
        await Task.WhenAll(tasks);
    }

    private Task SendAsync(SessionState s, string kind, object body, byte[]? requestId, CancellationToken ct) =>
        sessions.SendFrameAsync(s, Build(kind, JsonSerializer.SerializeToUtf8Bytes(body, Json), requestId ?? []), ct);

    private Task ErrorAsync(SessionState s, SocialRequestPayload req, string message, CancellationToken ct) =>
        SendAsync(s, "error", new { message, kind = Sanitize(req.Kind, 64) }, req.RequestId, ct);

    private static Frame Build(string kind, byte[] body, byte[] requestId) => new()
    {
        MsgType = MessageType.SocialEvent,
        Channel = Channel.Social,
        Payload = Codec.EncodePayload(new SocialEventPayload { Kind = kind, BodyJson = body, RequestId = requestId }),
    };

    private sealed class TokenBucket(double ratePerSecond, double capacity, DateTimeOffset now)
    {
        private readonly double _capacity = capacity;
        private double _tokens = capacity;
        private DateTimeOffset _last = now;

        public bool TryTake(DateTimeOffset at)
        {
            lock (this)
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
}
