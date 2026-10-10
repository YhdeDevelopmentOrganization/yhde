using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using YHDE.Server.Domain;
using YHDE.Server.Framing;
using YHDE.Server.Framing.Messages;

namespace YHDE.Server.Gateway;

// Live sessions and sending to everyone in a branch or project. Sending only
// queues: each connection's own task writes to its socket (Outbox.cs), so
// one stalled client never holds up the commit loop, presence or chat for
// anyone else.
public sealed class SessionManager(ILogger<SessionManager> logger) : ISessionManager
{
    private readonly ConcurrentDictionary<Guid, SessionState> _sessions = new();
    private long _slowDisconnects;

    // Connections cut off for being too slow since the server started (admin page).
    public long SlowDisconnects => Interlocked.Read(ref _slowDisconnects);

    public IReadOnlyCollection<SessionState> All => _sessions.Values.ToList();

    public void Register(SessionState session)
        => _sessions[session.SessionId] = session;

    public void Remove(Guid sessionId)
        => _sessions.TryRemove(sessionId, out _);

    public IEnumerable<SessionState> GetBranchSubscribers(Guid branchId)
        => _sessions.Values.Where(s => s.SubscribedBranchId == branchId);

    public IEnumerable<SessionState> GetProjectSessions(Guid projectId)
        => _sessions.Values.Where(s => s.SubscribedProjectId == projectId);

    // Broadcast a committed operation to every subscriber of the operation's branch.
    // Called by OperationProcessor AFTER the commit transaction (reliability.md).
    public Task BroadcastOpCommittedAsync(Operation op, CancellationToken ct)
    {
        var payload = new CommittedOpPayload
        {
            OpId = op.OpId.ToByteArray(),
            Seq = op.Seq,
            BranchId = op.BranchId.ToByteArray(),
            Type = op.Type,
            TargetId = op.TargetId.ToByteArray(),
            PayloadJson = Encoding.UTF8.GetBytes(op.Payload),
            ActorId = op.ActorId.ToByteArray(),
            SessionId = op.SessionId.ToByteArray(),
            ClientOpRef = op.ClientOpRef.ToByteArray(),
            ParentSeq = op.ParentSeq,
            CreatedAtUnixMs = new DateTimeOffset(op.CreatedAt).ToUnixTimeMilliseconds(),
            Signature = op.Signature,
        };

        var frame = new Frame
        {
            MsgType = MessageType.OpCommitted,
            Channel = Channel.Ops,
            SeqOrAck = op.Seq,
            Payload = Codec.EncodePayload(payload),
        };
        var wire = Codec.Encode(frame);

        foreach (var s in GetBranchSubscribers(op.BranchId)) Post(s, wire, droppable: false);
        return Task.CompletedTask;
    }

    // Send an OpRejected message to the originating session only.
    public async Task SendRejectedAsync(
        SessionState session, Guid clientOpRef, Guid opId,
        string code, string reason, CancellationToken ct)
    {
        var payload = new OpRejectedPayload
        {
            ClientOpRef = clientOpRef.ToByteArray(),
            OpId = opId.ToByteArray(),
            Code = code,
            Reason = reason,
        };
        var frame = new Frame
        {
            MsgType = MessageType.OpRejected,
            Channel = Channel.Ops,
            ClientOpRef = clientOpRef.ToByteArray(),
            Payload = Codec.EncodePayload(payload),
        };
        await SendAsync(session, Codec.Encode(frame), ct);
    }

    // Send a Welcome message to a newly authenticated session.
    public async Task SendWelcomeAsync(SessionState session, CancellationToken ct, Projects.ProjectInfo? project = null)
    {
        var payload = new WelcomePayload
        {
            NegotiatedVersion = Frame.ProtocolVersion,
            SessionId = session.SessionId.ToByteArray(),
            ServerCapabilities = ServerInfo.Capabilities,
            ServerVersion = ServerInfo.Version,
            ProjectId = project?.ProjectId.ToByteArray() ?? [],
            ProjectName = project?.Name ?? "",
            BranchId = project?.MainBranchId.ToByteArray() ?? [],
        };
        var frame = new Frame
        {
            MsgType = MessageType.Welcome,
            Channel = Channel.System,
            Payload = Codec.EncodePayload(payload),
        };
        await SendAsync(session, Codec.Encode(frame), ct);
    }

    // Send a SyncState to bring the subscriber up to head (network_protocol.md).
    public async Task SendSyncStateAsync(
        SessionState session,
        Guid branchId,
        long headSeq,
        IReadOnlyList<CommittedOpPayload> tailOps,
        bool hasMore,
        CancellationToken ct)
    {
        var payload = new SyncStatePayload
        {
            BranchId = branchId.ToByteArray(),
            SnapshotSeq = 0,        // no snapshots yet
            SnapshotRef = null,
            TailOps = tailOps.ToArray(),
            HeadSeq = headSeq,
            HasMore = hasMore,
        };
        var frame = new Frame
        {
            MsgType = MessageType.SyncState,
            Channel = Channel.Ops,
            SeqOrAck = headSeq,
            Payload = Codec.EncodePayload(payload),
        };
        await SendAsync(session, Codec.Encode(frame), ct);
    }

    // Send a Pong in reply to a Ping.
    public async Task SendPongAsync(SessionState session, byte[] token, CancellationToken ct)
    {
        var frame = new Frame
        {
            MsgType = MessageType.Pong,
            Channel = Channel.System,
            Payload = Codec.EncodePayload(new PingPongPayload { Token = token }),
        };
        await SendAsync(session, Codec.Encode(frame), ct);
    }

    // Send an Error frame.
    public async Task SendErrorAsync(SessionState session, int code, string message, bool retryable, CancellationToken ct)
    {
        var frame = new Frame
        {
            MsgType = MessageType.Error,
            Channel = Channel.System,
            Payload = Codec.EncodePayload(new ErrorPayload { Code = code, Message = message, Retryable = retryable }),
        };
        await SendAsync(session, Codec.Encode(frame), ct);
    }

    // Send an arbitrary frame to one session (presence, chat): never waits.
    public Task SendFrameAsync(SessionState session, Frame frame, CancellationToken ct)
    {
        Post(session, Codec.Encode(frame), droppable: false);
        return Task.CompletedTask;
    }

    public Task SendEncodedAsync(SessionState session, byte[] wire, CancellationToken ct, bool droppable = false)
    {
        Post(session, wire, droppable);
        return Task.CompletedTask;
    }

    public void Disconnect(SessionState session, string reason, bool slow = false)
    {
        if (slow) Interlocked.Increment(ref _slowDisconnects);
        logger.LogWarning("Closing session {SessionId} ({Name}): {Reason}", session.SessionId, session.MemberName, reason);
        try
        {
            session.Outbox.Complete();
            session.Socket.Abort();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Abort of session {SessionId} failed", session.SessionId);
        }
    }

    // Queues for another person's connection; never waits for their network.
    private void Post(SessionState session, byte[] wire, bool droppable)
    {
        if (session.Socket.State != WebSocketState.Open) return;
        if (session.Outbox.TryPost(wire, droppable)) return;
        Disconnect(session, $"too slow: {session.Outbox.QueuedBytes} bytes waiting to be sent", slow: true);
    }

    // Queues a reply to the session's own request. Waits while its queue is
    // full, which slows only that session's own read loop.
    private async Task SendAsync(SessionState session, byte[] wire, CancellationToken ct)
    {
        if (session.Socket.State != WebSocketState.Open) return;
        await session.Outbox.PostAsync(wire, ct);
    }
}
