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
    Teams.StorageQuota? quota = null,
    Assets.IProjectBlobs? projectBlobs = null,
    Assets.BlobStore? blobs = null)
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
        // This connection's own sender (Outbox.cs): nobody else waits for its network.
        var sender = Task.Run(() => session.Outbox.RunAsync(socket, session.WriteLock,
            reason => sessionManager.Disconnect(session, "too slow: " + reason, slow: true), ct));

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

            // Let the last replies go out (briefly), then close.
            session.Outbox.Complete();
            await Task.WhenAny(sender, Task.Delay(TimeSpan.FromSeconds(5)));
            await CloseAsync(session, WebSocketCloseStatus.NormalClosure, "bye");
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
                    session.Outbox.Complete();
                    await Task.Delay(200, ct);
                    await CloseAsync(session, WebSocketCloseStatus.MessageTooBig, "Message too large");
                    return;
                }
            } while (!result.EndOfMessage);

            session.LastReceivedTicks = Environment.TickCount64;
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

        try
        {
            await HandleFrameAsync(session, frame, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or WebSocketException))
        {
            // One request failed (a malformed field, the database busy for a
            // moment): answer it, keep the connection. The editor sends what
            // it is still waiting for again (reliability.md).
            logger.LogError(ex, "Session {SessionId}: {Type} failed", session.SessionId, frame.MsgType);
            await sessionManager.SendErrorAsync(session, 503, "The server could not handle that just now; it will be sent again.", true, ct);
        }
    }

    private async Task HandleFrameAsync(SessionState session, Frame frame, CancellationToken ct)
    {
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
        if (!TryId(msg.ProjectId, out var projectId) || !TryId(msg.BranchId, out var branchId))
        {
            await sessionManager.SendErrorAsync(session, 400, "Subscribe needs a 16-byte project and branch id.", false, ct);
            return;
        }

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
        // streamed in bounded pages. The head is read again now that the session
        // receives broadcasts: an op committed before this point is in the
        // catch-up, one after it arrives live (clients de-duplicate by seq).
        // Reading it before subscribing would lose ops committed in between.
        var head = Math.Max(branch.HeadSeq, (await branchRepo.GetAsync(branchId, ct))?.HeadSeq ?? 0);
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

        if (!TryId(msg.ClientOpRef, out var clientOpRef) || !TryId(msg.OpId, out var opId) || !TryId(msg.TargetId, out var targetId))
        {
            await sessionManager.SendErrorAsync(session, 400, "An operation needs 16-byte ids.", false, ct);
            return;
        }

        // View only: they can look, chat and comment, but not change it.
        if (!session.Grant.CanEdit(session.SubscribedProjectId!.Value))
        {
            await sessionManager.SendRejectedAsync(session, clientOpRef, opId,
                "Forbidden", "You can view this project but not change it. Ask the project's owner for edit access.", ct);
            return;
        }

        // A file's bytes must be in this project (uploaded here, or already in
        // its log) before an operation may use them (assets.md).
        var projectId = session.SubscribedProjectId!.Value;
        var payloadText = Encoding.UTF8.GetString(msg.PayloadJson);
        var fileHash = msg.Type is OperationType.RegisterAsset or OperationType.UpdateAsset ? HashOf(payloadText) : null;
        Assets.ProjectBlob? held = null;
        if (fileHash is not null && projectBlobs is not null)
        {
            held = await projectBlobs.GetAsync(projectId, fileHash, ct);
            // The operator's server key may use any stored file (older add-ons
            // upload without naming the project).
            if (held is null && ReferenceEquals(session.Grant, AccessGrant.AllProjects) && blobs?.SizeOf(fileHash) is { } size)
            {
                await projectBlobs.AddAsync(projectId, fileHash, size, "server", referenced: false, ct);
                held = new Assets.ProjectBlob(size, false);
            }
            if (held is null)
            {
                await sessionManager.SendRejectedAsync(session, clientOpRef, opId,
                    nameof(RejectionCode.AssetMissing), "The file's bytes have not been uploaded to this project.", ct);
                return;
            }
        }

        // A new file must fit in the project owner's storage. An upload to
        // this project was counted when it was uploaded.
        if (quota is not null && await quota.RefuseAsync(projectId, msg.Type, payloadText, ct, uploadCounted: held is { Referenced: false }) is { } full)
        {
            await sessionManager.SendRejectedAsync(session, clientOpRef, opId, "StorageFull", full, ct);
            return;
        }

        var submission = new OperationSubmission(
            OpId: opId,
            Type: msg.Type,
            TargetId: targetId,
            Payload: payloadText,
            ClientOpRef: clientOpRef,
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
        else if (fileHash is not null && held is { Referenced: false } && projectBlobs is not null)
        {
            // Committed (and broadcast inside ProcessAsync): the upload is in use now.
            await projectBlobs.MarkReferencedAsync(projectId, fileHash, ct);
        }
    }

    private static string? HashOf(string payload)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("h", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.String
                && Assets.BlobStore.IsValidHash(h.GetString()) ? h.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // the processor refuses it for being malformed
        }
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

        var ids = (msg.Ops ?? []).SelectMany(o => new[] { o.OpId, o.TargetId, o.ClientOpRef }).Append(msg.RequestId).Concat(msg.UndoOf ?? []);
        if (ids.Any(b => b is not { Length: 16 }))
        {
            await sessionManager.SendErrorAsync(session, 400, "An undo needs 16-byte ids.", false, ct);
            return;
        }
        var request = new UndoService.Request(
            RequestId: new Guid(msg.RequestId),
            Kind: msg.Kind,
            UndoOf: msg.UndoOf!.Select(b => new Guid(b)).ToList(),
            Ops: msg.Ops!.Select(o => new OperationSubmission(
                OpId: new Guid(o.OpId),
                Type: o.Type,
                TargetId: new Guid(o.TargetId),
                Payload: Encoding.UTF8.GetString(o.PayloadJson),
                ClientOpRef: new Guid(o.ClientOpRef),
                ParentSeq: o.ParentSeq)).ToList());

        // Undoing a delete brings a file back: it must fit in the owner's storage.
        UndoService.Result? result = null;
        if (quota is not null)
        {
            foreach (var op in request.Ops)
            {
                if (await quota.RefuseAsync(session.SubscribedProjectId!.Value, op.Type, op.Payload, ct) is { } full)
                {
                    result = UndoService.Result.Refused("StorageFull", full);
                    break;
                }
            }
        }
        result ??= await undo.ProcessAsync(
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

    private static bool TryId(byte[]? bytes, out Guid id)
    {
        id = bytes is { Length: 16 } ? new Guid(bytes) : Guid.Empty;
        return bytes is { Length: 16 };
    }

    // Closes the socket as the one writer (the sender may be mid-message).
    private static async Task CloseAsync(SessionState session, WebSocketCloseStatus status, string description)
    {
        var socket = session.Socket;
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        if (!await session.WriteLock.WaitAsync(TimeSpan.FromSeconds(5))) { socket.Abort(); return; }
        try { await socket.CloseAsync(status, description, CancellationToken.None); }
        catch { /* already closed */ }
        finally { session.WriteLock.Release(); }
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
