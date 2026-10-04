using YHDE.Server.Admin;
using System.Net.WebSockets;
using System.Text;
using YHDE.Server.Domain;
using YHDE.Server.Framing;
using YHDE.Server.Framing.Messages;
using YHDE.Server.Operations;
using YHDE.Server.Persistence.Repositories;
using YHDE.Server.Presence;
using YHDE.Server.Projects;
using YHDE.Server.Social;

namespace YHDE.Server.Gateway;

// One WebSocket session from connect to close (network_protocol.md). The
// connection was already admitted by AccessGate, whose grant says which
// projects it may open and, for a sign-in, who it is.
//
// Each connection has its own read loop; sends are serialised per session by
// SessionState.WriteLock.
public sealed class WebSocketGateway(
    ISessionManager sessionManager,
    OperationProcessor processor,
    UndoService undo,
    PresenceService presence,
    SocialService social,
    IProjectStore projects,
    IBranchRepository branchRepo,
    IOperationRepository opRepo,
    IConfiguration configuration,
    ILogger<WebSocketGateway> logger,
    SessionLog? sessionLog = null,
    Teams.StorageQuota? quota = null)
{
    // Largest accepted client message (a big embedded resource, e.g. tile data
    // or a mesh, can be several MiB). Enforced while reading, before buffering.
    private readonly int _maxFrameBytes =
        Math.Max(64 * 1024, configuration.GetValue("Yhde:MaxFrameSizeBytes", 16 * 1024 * 1024));

    // Catch-up is streamed in pages so a long log never becomes one giant frame
    // (network_protocol.md).
    public const int SyncPageSize = 256;
    public const long SyncPageBytes = 8 * 1024 * 1024;

    // Entry point: called by the ASP.NET Core WebSocket middleware for each accepted connection.
    public async Task HandleAsync(WebSocket socket, string? authHeader, AccessGrant grant, CancellationToken ct)
    {
        // Without a sign-in, changes are made as the shared dev identity.
        var actorId = DevIdentity.ResolveActorId(authHeader);

        var session = new SessionState
        {
            Socket = socket,
            // A signed-in editor acts as its account: changes are theirs.
            ActorId = grant.UserId ?? actorId,
            Grant = grant,
        };
        session.MemberId = grant.UserId ?? session.SessionId; // until Hello names the member
        if (grant.UserName is { Length: > 0 } name) session.MemberName = name;
        sessionManager.Register(session);

        logger.LogInformation("Session {SessionId} opened for actor {ActorId}", session.SessionId, session.ActorId);

        try
        {
            ProjectInfo? invited = grant.ProjectId is { } pid ? await projects.GetAsync(pid, ct) : null;
            await sessionManager.SendWelcomeAsync(session, ct, invited);
            await ReadLoopAsync(session, ct);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            // Normal disconnection: no action required.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error in session {SessionId}", session.SessionId);
        }
        finally
        {
            sessionManager.Remove(session.SessionId);
            social.Forget(session.SessionId);
            if (sessionLog is not null && session.IsSubscribed) await sessionLog.EndAsync(session.SessionId);
            try { await presence.RemoveAsync(session.SessionId, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Presence cleanup failed for {SessionId}", session.SessionId); }
            logger.LogInformation("Session {SessionId} closed", session.SessionId);

            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch { /* already closed */ }
            }
        }
    }

    private async Task ReadLoopAsync(SessionState session, CancellationToken ct)
    {
        var socket = session.Socket;

        // Buffer for reading binary WebSocket messages.
        // WebSocket.ReceiveAsync doesn't use the length-prefix format; it delivers
        // each logical message in one or more segments and signals EndOfMessage.
        var buf = new byte[16 * 1024]; // messages larger than this arrive in parts
        using var ms = new System.IO.MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogDebug("Session {SessionId} sent a Close frame", session.SessionId);
                    return;
                }
                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    logger.LogWarning("Session {SessionId} sent a non-binary frame: ignored", session.SessionId);
                    continue;
                }
                ms.Write(buf, 0, result.Count);
                if (ms.Length > _maxFrameBytes + 4)
                {
                    logger.LogWarning("Session {SessionId} sent a message over {Max} bytes: closing", session.SessionId, _maxFrameBytes);
                    await sessionManager.SendErrorAsync(session, 413, "Message too large.", false, ct);
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", ct);
                    return;
                }
            } while (!result.EndOfMessage);

            if (ms.Length == 0) continue;

            var messageBytes = ms.ToArray();
            await DispatchAsync(session, messageBytes, ct);
        }
    }

    private async Task DispatchAsync(SessionState session, byte[] messageBytes, CancellationToken ct)
    {
        // The wire format is: [frame_len: uint32 BE] [MessagePack Frame bytes].
        // ASP.NET Core WebSocket delivers one logical message per ReceiveAsync call,
        // so the frame_len prefix is present but we can also just decode the remainder.
        if (messageBytes.Length < 4)
        {
            await sessionManager.SendErrorAsync(session, 400, "Frame too short.", false, ct);
            return;
        }

        var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(messageBytes.AsSpan(0, 4));
        if (len > _maxFrameBytes || (uint)(messageBytes.Length - 4) < len)
        {
            await sessionManager.SendErrorAsync(session, 400, "Frame length mismatch.", false, ct);
            return;
        }

        Frame frame;
        try
        {
            frame = Codec.Decode(messageBytes.AsMemory(4, (int)len));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session {SessionId} sent a malformed frame", session.SessionId);
            await sessionManager.SendErrorAsync(session, 400, "Malformed frame.", false, ct);
            return;
        }

        if (frame.Version != Frame.ProtocolVersion)
        {
            await sessionManager.SendErrorAsync(
                session, 426, $"Unsupported protocol version {frame.Version}. Expected {Frame.ProtocolVersion}.", false, ct);
            return;
        }

        switch (frame.MsgType)
        {
            case MessageType.Hello:
                // Welcome was already sent; Hello names the member behind the
                // session (for chat and comments). A signed-in session keeps
                // its account's id and name whatever Hello says.
                HandleHello(session, frame);
                break;

            case MessageType.SocialRequest:
                await HandleSocialAsync(session, frame, ct);
                break;

            case MessageType.Ping:
                var ping = Codec.DecodePayload<PingPongPayload>(frame.Payload);
                await sessionManager.SendPongAsync(session, ping.Token, ct);
                break;

            case MessageType.Subscribe:
                await HandleSubscribeAsync(session, frame, ct);
                break;

            case MessageType.SubmitOp:
                await HandleSubmitOpAsync(session, frame, ct);
                break;

            case MessageType.UndoRequest:
                await HandleUndoRequestAsync(session, frame, ct);
                break;

            case MessageType.Ack:
                var ack = Codec.DecodePayload<AckPayload>(frame.Payload);
                session.LastAckedSeq = Math.Max(session.LastAckedSeq, ack.Seq);
                break;

            case MessageType.PresenceUpdate:
                await HandlePresenceUpdateAsync(session, frame, ct);
                break;

            default:
                logger.LogDebug("Session {SessionId} sent unhandled message type {Type}: ignored",
                    session.SessionId, frame.MsgType);
                break;
        }
    }

    private async Task HandleSubscribeAsync(SessionState session, Frame frame, CancellationToken ct)
    {
        var msg = Codec.DecodePayload<SubscribePayload>(frame.Payload);
        var projectId = new Guid(msg.ProjectId);
        var branchId = new Guid(msg.BranchId);

        if (!session.Grant.Allows(projectId))
        {
            await sessionManager.SendErrorAsync(session, 403, "Your invite code is for another project.", false, ct);
            return;
        }
        var branch = await branchRepo.GetAsync(branchId, ct);
        if (branch is null || branch.ProjectId != projectId)
        {
            await sessionManager.SendErrorAsync(session, 404,
                "That project does not exist on this server (it may have been deleted). Check the invite code with your server admin.", false, ct);
            return;
        }
        var project = await projects.GetAsync(projectId, ct);
        if (project?.ArchivedAt is not null)
        {
            await sessionManager.SendErrorAsync(session, 410, "This project was archived by the server admin.", false, ct);
            return;
        }

        // Switching branches: the old branch's peers see this session leave.
        if (session.SubscribedBranchId is { } previous && previous != branchId)
            await presence.RemoveAsync(session.SessionId, ct);

        session.SubscribedProjectId = projectId;
        session.SubscribedBranchId = branchId;
        session.LastAckedSeq = msg.LastAckedSeq;
        if (sessionLog is not null) await sessionLog.StartAsync(session);

        // Fetch the tail needed to bring the client up to head (network_protocol.md),
        // streamed in bounded pages. Ops committed while paging are also broadcast
        // live to this (already subscribed) session; clients de-duplicate by seq.
        var head = branch.HeadSeq;
        var from = Math.Clamp(msg.LastAckedSeq, 0, head);
        var sent = 0;
        var chunk = new List<CommittedOpPayload>();
        long chunkBytes = 0;
        do
        {
            var to = Math.Min(head, from + SyncPageSize);
            var page = to > from
                ? await opRepo.GetTailAsync(branchId, from, to, ct)
                : [];
            foreach (var op in page)
            {
                var payload = MapToCommittedPayload(op);
                // Pages are bounded by bytes as well as count: a few large ops
                // (embedded meshes, tile data) must not form one giant frame.
                if (chunk.Count > 0 && chunkBytes + payload.PayloadJson.Length > SyncPageBytes)
                {
                    await sessionManager.SendSyncStateAsync(session, branchId, head, chunk, hasMore: true, ct);
                    chunk = [];
                    chunkBytes = 0;
                }
                chunk.Add(payload);
                chunkBytes += payload.PayloadJson.Length;
            }
            sent += page.Count;
            from = to;
        } while (from < head);
        await sessionManager.SendSyncStateAsync(session, branchId, head, chunk, hasMore: false, ct);

        await presence.SendSnapshotAsync(session, ct);
        await social.SendInitialAsync(session, ct);

        logger.LogInformation(
            "Session {SessionId} subscribed to branch {BranchId} (head={Head}, tail={TailCount})",
            session.SessionId, branchId, head, sent);
    }

    private void HandleHello(SessionState session, Frame frame)
    {
        HelloPayload hello;
        try
        {
            hello = Codec.DecodePayload<HelloPayload>(frame.Payload);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Session {SessionId} sent an unparseable Hello", session.SessionId);
            return;
        }
        if (session.Grant.UserId is { } account)
        {
            // Signed in: the account decides who this is, never the editor.
            session.MemberId = account;
            session.MemberName = session.Grant.UserName is { Length: > 0 } n ? n : "Member";
        }
        else
        {
            if (hello.MemberId is { Length: 16 } id && new Guid(id) != Guid.Empty)
                session.MemberId = new Guid(id);
            session.MemberName = PresenceService.SanitizeName(hello.DisplayName);
        }
        session.ClientVersion = PresenceService.SanitizeText(hello.ClientVersion, 32);
        logger.LogInformation("Session {SessionId} is {Name} (add-on {Version})",
            session.SessionId, session.MemberName, session.ClientVersion.Length > 0 ? session.ClientVersion : "unknown");
    }

    private async Task HandleSocialAsync(SessionState session, Frame frame, CancellationToken ct)
    {
        SocialRequestPayload msg;
        try
        {
            msg = Codec.DecodePayload<SocialRequestPayload>(frame.Payload);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Session {SessionId} sent an unparseable SocialRequest", session.SessionId);
            return;
        }
        await social.HandleAsync(session, msg, ct);
    }

    private async Task HandlePresenceUpdateAsync(SessionState session, Frame frame, CancellationToken ct)
    {
        PresenceUpdatePayload msg;
        try
        {
            msg = Codec.DecodePayload<PresenceUpdatePayload>(frame.Payload);
        }
        catch (Exception ex)
        {
            // Presence may lose updates anyway: a malformed one is dropped.
            logger.LogDebug(ex, "Session {SessionId} sent an unparseable PresenceUpdate", session.SessionId);
            return;
        }
        await presence.HandleUpdateAsync(session, msg, ct);
    }

    private async Task HandleSubmitOpAsync(SessionState session, Frame frame, CancellationToken ct)
    {
        if (!session.IsSubscribed)
        {
            await sessionManager.SendErrorAsync(session, 400, "Must subscribe before submitting operations.", false, ct);
            return;
        }

        SubmitOpPayload msg;
        try
        {
            msg = Codec.DecodePayload<SubmitOpPayload>(frame.Payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session {SessionId} sent an unparseable SubmitOp", session.SessionId);
            await sessionManager.SendErrorAsync(session, 400, "Unparseable SubmitOp payload.", false, ct);
            return;
        }

        // View only: they can look, chat and comment, but not change it.
        if (!session.Grant.CanEdit(session.SubscribedProjectId!.Value))
        {
            await sessionManager.SendRejectedAsync(session, new Guid(msg.ClientOpRef), new Guid(msg.OpId),
                "Forbidden", "You can view this project but not change it. Ask the project's owner for edit access.", ct);
            return;
        }

        // A new file must fit in the project owner's storage.
        if (quota is not null && await quota.RefuseAsync(session.SubscribedProjectId!.Value, msg.Type, Encoding.UTF8.GetString(msg.PayloadJson), ct) is { } full)
        {
            await sessionManager.SendRejectedAsync(session, new Guid(msg.ClientOpRef), new Guid(msg.OpId), "StorageFull", full, ct);
            return;
        }

        var submission = new OperationSubmission(
            OpId: new Guid(msg.OpId),
            Type: msg.Type,
            TargetId: new Guid(msg.TargetId),
            Payload: Encoding.UTF8.GetString(msg.PayloadJson),
            ClientOpRef: new Guid(msg.ClientOpRef),
            ParentSeq: msg.ParentSeq);

        var result = await processor.ProcessAsync(
            submission, session.SubscribedBranchId!.Value,
            session.ActorId, session.SessionId, ct);

        if (result is SubmitResult.Rejected rejected)
        {
            await sessionManager.SendRejectedAsync(
                session, rejected.ClientOpRef, rejected.OpId,
                rejected.Code.ToString(), rejected.Reason, ct);
        }
        // Committed: broadcast already happened inside ProcessAsync.
    }

    private async Task HandleUndoRequestAsync(SessionState session, Frame frame, CancellationToken ct)
    {
        if (!session.IsSubscribed)
        {
            await sessionManager.SendErrorAsync(session, 400, "Must subscribe before undoing.", false, ct);
            return;
        }
        if (!session.Grant.CanEdit(session.SubscribedProjectId!.Value))
        {
            await sessionManager.SendErrorAsync(session, 403, "You can view this project but not change it.", false, ct);
            return;
        }

        UndoRequestPayload msg;
        try
        {
            msg = Codec.DecodePayload<UndoRequestPayload>(frame.Payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session {SessionId} sent an unparseable UndoRequest", session.SessionId);
            await sessionManager.SendErrorAsync(session, 400, "Unparseable UndoRequest payload.", false, ct);
            return;
        }

        var request = new UndoService.Request(
            RequestId: new Guid(msg.RequestId),
            Kind: msg.Kind,
            UndoOf: msg.UndoOf.Select(b => new Guid(b)).ToList(),
            Ops: msg.Ops.Select(o => new OperationSubmission(
                OpId: new Guid(o.OpId),
                Type: o.Type,
                TargetId: new Guid(o.TargetId),
                Payload: Encoding.UTF8.GetString(o.PayloadJson),
                ClientOpRef: new Guid(o.ClientOpRef),
                ParentSeq: o.ParentSeq)).ToList());

        var result = await undo.ProcessAsync(
            request, session.SubscribedBranchId!.Value, session.ActorId, session.SessionId, ct);

        var reply = new Frame
        {
            MsgType = MessageType.UndoResult,
            Channel = Channel.Ops,
            ClientOpRef = msg.RequestId,
            Payload = Codec.EncodePayload(new UndoResultPayload
            {
                RequestId = msg.RequestId,
                Status = result.Status,
                Reason = result.Reason,
                Code = result.Code,
            }),
        };
        await sessionManager.SendFrameAsync(session, reply, ct);
    }

    private static CommittedOpPayload MapToCommittedPayload(Operation op) => new()
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
}
