using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Channel = YHDE.Server.Framing.Channel;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using YHDE.Server.Domain;
using YHDE.Server.Framing;
using YHDE.Server.Framing.Messages;
using YHDE.Server.Gateway;
using YHDE.Server.Operations;
using YHDE.Server.Persistence.Repositories;
using YHDE.Server.Presence;
using YHDE.Server.Projects;
using YHDE.Server.Social;
using YHDE.Server.Tests.Assets;
using YHDE.Server.Tests.Social;

namespace YHDE.Server.Tests.Gateway;

// A WebSocket in memory: frames the test puts in come out of ReceiveAsync,
// and everything sent is recorded. A stalled one never finishes a send.
public sealed class FakeSocket : WebSocket
{
    private readonly System.Threading.Channels.Channel<byte[]> _in = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    private readonly System.Threading.Channels.Channel<byte[]> _out = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    private WebSocketState _state = WebSocketState.Open;

    public bool Stalled { get; set; }
    public ChannelReader<byte[]> Sent => _out.Reader;

    public void Receive(Frame frame) => _in.Writer.TryWrite(Codec.Encode(frame));

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
        _in.Writer.TryComplete();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        _in.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override void Dispose() { }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            var msg = await _in.Reader.ReadAsync(cancellationToken);
            msg.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(msg.Length, WebSocketMessageType.Binary, true);
        }
        catch (ChannelClosedException)
        {
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (Stalled) await Task.Delay(Timeout.Infinite, cancellationToken);
        _out.Writer.TryWrite(buffer.ToArray());
    }

    // The next frame of the given type the server sent, within `seconds`.
    public async Task<Frame> NextAsync(MessageType type, double seconds = 5)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true)
        {
            var wire = await _out.Reader.ReadAsync(cts.Token);
            var frame = Codec.Decode(wire.AsMemory(4));
            if (frame.MsgType == type) return frame;
        }
    }
}

// An operation log in memory, with consecutive seqs per branch.
public sealed class InMemoryOperationRepository : IOperationRepository
{
    private readonly Lock _gate = new();
    private readonly List<Operation> _ops = [];
    public Func<Task>? BeforeCommit { get; set; }

    private Operation Add(OperationSubmission s, Guid branchId, Guid actorId, Guid sessionId)
    {
        var seq = _ops.Count(o => o.BranchId == branchId) + 1;
        var op = new Operation(s.OpId, seq, branchId, s.Type, s.TargetId, s.Payload, actorId, sessionId, s.ClientOpRef, s.ParentSeq, DateTime.UtcNow, []);
        _ops.Add(op);
        return op;
    }

    public long Head(Guid branchId) { lock (_gate) return _ops.Count(o => o.BranchId == branchId); }

    public async Task<Operation> CommitAsync(OperationSubmission submission, Guid branchId, Guid actorId, Guid sessionId, CancellationToken ct) =>
        (await CommitBatchAsync([submission], branchId, actorId, sessionId, ct))[0];

    public Task<IReadOnlyList<Operation>> GetTailAsync(Guid branchId, long fromSeqExclusive, long toSeqInclusive, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Operation>>(_ops.Where(o => o.BranchId == branchId && o.Seq > fromSeqExclusive && o.Seq <= toSeqInclusive).ToList());
    }

    public Task<bool> ExistsAsync(Guid opId, CancellationToken ct) { lock (_gate) return Task.FromResult(_ops.Any(o => o.OpId == opId)); }

    public async Task<IReadOnlyList<Operation>> CommitBatchAsync(IReadOnlyList<OperationSubmission> submissions, Guid branchId, Guid actorId, Guid sessionId, CancellationToken ct)
    {
        if (BeforeCommit is not null) await BeforeCommit();
        lock (_gate) return submissions.Select(s => Add(s, branchId, actorId, sessionId)).ToList();
    }

    public async Task<IReadOnlyList<Operation?>> CommitGroupAsync(IReadOnlyList<CommitItem> items, Guid branchId, CancellationToken ct)
    {
        if (BeforeCommit is not null) await BeforeCommit();
        lock (_gate)
            return items.Select(i => _ops.Any(o => o.OpId == i.Submission.OpId) ? null : Add(i.Submission, branchId, i.ActorId, i.SessionId)).ToList();
    }

    public Task<IReadOnlyList<Operation>> GetByIdsAsync(Guid branchId, IReadOnlyCollection<Guid> opIds, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<Operation>>(_ops.Where(o => o.BranchId == branchId && opIds.Contains(o.OpId)).ToList());
    }

    public Task<Operation?> GetLatestAssetOpAsync(Guid branchId, Guid targetId, CancellationToken ct) => Task.FromResult<Operation?>(null);

    public Task<IReadOnlyList<string>> LivePathsDifferingInCaseAsync(Guid branchId, string path, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<Operation>> GetTargetOpsAsync(Guid branchId, Guid targetId, long afterSeq, string type, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Operation>>([]);
}

// Nobody waits for a stalled
// connection, nothing is lost while subscribing, undo keeps log order, and
// a bad request is answered without closing the connection.
public sealed class GatewayResilienceTests : IDisposable
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid BranchId = Guid.NewGuid();
    private readonly TempBlobStore _blobs = new();
    private readonly CancellationTokenSource _stop = new();

    public void Dispose()
    {
        _stop.Cancel();
        _blobs.Dispose();
    }

    private static SessionState Session(FakeSocket socket, TimeSpan? timeout = null) => new()
    {
        Socket = socket,
        Outbox = new Outbox(timeout),
        SubscribedProjectId = Project,
        SubscribedBranchId = BranchId,
    };

    private void StartSender(SessionManager sessions, SessionState s) =>
        _ = Task.Run(() => s.Outbox.RunAsync(s.Socket, s.WriteLock, r => sessions.Disconnect(s, r, slow: true), _stop.Token));

    private static Operation Op(long seq) => new(Guid.NewGuid(), seq, BranchId, "ChangeProperty", Guid.NewGuid(), "{}",
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, DateTime.UtcNow, []);

    [Fact]
    public async Task A_stalled_client_never_holds_up_anyone_and_is_cut_off()
    {
        var sessions = new SessionManager(NullLogger<SessionManager>.Instance);
        var fast = Session(new FakeSocket(), TimeSpan.FromMilliseconds(300));
        var stuck = Session(new FakeSocket { Stalled = true }, TimeSpan.FromMilliseconds(300));
        sessions.Register(fast);
        sessions.Register(stuck);
        StartSender(sessions, fast);
        StartSender(sessions, stuck);

        var clock = Stopwatch.StartNew();
        for (var i = 1; i <= 50; i++) await sessions.BroadcastOpCommittedAsync(Op(i), default);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "broadcasting only queues");

        for (var i = 1; i <= 50; i++)
            (await ((FakeSocket)fast.Socket).NextAsync(MessageType.OpCommitted)).SeqOrAck.Should().Be(i);

        // The stuck one cannot take its first message within the send timeout.
        var until = DateTime.UtcNow.AddSeconds(5);
        while (stuck.Socket.State == WebSocketState.Open && DateTime.UtcNow < until) await Task.Delay(20);
        stuck.Socket.State.Should().Be(WebSocketState.Aborted);
        fast.Socket.State.Should().Be(WebSocketState.Open);
        sessions.SlowDisconnects.Should().Be(1);
    }

    [Fact]
    public void A_client_whose_queue_fills_up_is_cut_off_at_once()
    {
        var sessions = new SessionManager(NullLogger<SessionManager>.Instance);
        var stuck = Session(new FakeSocket { Stalled = true }); // no sender: nothing leaves the queue
        sessions.Register(stuck);
        var big = new byte[1024 * 1024];
        for (var i = 0; i < 70 && stuck.Socket.State == WebSocketState.Open; i++)
            sessions.SendEncodedAsync(stuck, big, default);
        stuck.Socket.State.Should().Be(WebSocketState.Aborted);
        sessions.SlowDisconnects.Should().Be(1);
    }

    [Fact]
    public async Task Cursor_updates_are_dropped_for_a_lagging_peer_and_never_block_the_sender()
    {
        var sessions = new SessionManager(NullLogger<SessionManager>.Instance);
        var me = Session(new FakeSocket());
        var stuck = Session(new FakeSocket { Stalled = true });
        sessions.Register(me);
        sessions.Register(stuck);
        // Time moves a second per update, so the rate limit never drops one here.
        var presence = new PresenceService(sessions, new Ticking(), NullLogger<PresenceService>.Instance);

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 2000; i++)
            await presence.HandleUpdateAsync(me, new PresenceUpdatePayload { DisplayName = "Ada", Scene = "res://a.tscn" }, default);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        stuck.Outbox.QueuedMessages.Should().BeLessThanOrEqualTo(Outbox.PresenceDropThreshold + 1, "old cursor positions are dropped");
        stuck.Socket.State.Should().Be(WebSocketState.Open, "dropping cursor updates is not a reason to cut someone off");
    }

    private sealed class Ticking : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }

    private (WebSocketGateway Gateway, InMemoryOperationRepository Ops, SessionManager Sessions, IBranchRepository Branches) Gateway()
    {
        var sessions = new SessionManager(NullLogger<SessionManager>.Instance);
        var ops = new InMemoryOperationRepository();
        var committer = new BranchCommitter(ops, sessions, NullLogger<BranchCommitter>.Instance);
        var processor = new OperationProcessor(committer, ops, _blobs.Store, NullLogger<OperationProcessor>.Instance);
        var undo = new UndoService(ops, sessions, _blobs.Store, NullLogger<UndoService>.Instance, committer);
        var presence = new PresenceService(sessions, TimeProvider.System, NullLogger<PresenceService>.Instance);
        var social = new SocialService(sessions, new InMemorySocialStore(), TimeProvider.System, NullLogger<SocialService>.Instance);
        var projects = Substitute.For<IProjectStore>();
        projects.GetAsync(Project, Arg.Any<CancellationToken>()).Returns(new ProjectInfo(Project, "P", DateTimeOffset.UtcNow, null, BranchId));
        var branches = Substitute.For<IBranchRepository>();
        branches.GetAsync(BranchId, Arg.Any<CancellationToken>())
            .Returns(_ => new Branch(BranchId, Project, "main", ops.Head(BranchId), null, null, DateTime.UtcNow));
        var gateway = new WebSocketGateway(sessions, processor, undo, presence, social, projects, branches, ops,
            new ConfigurationBuilder().Build(), NullLogger<WebSocketGateway>.Instance);
        return (gateway, ops, sessions, branches);
    }

    private static Frame Message<T>(MessageType type, T payload, Channel channel = Channel.Ops) => new()
    {
        MsgType = type,
        Channel = channel,
        Payload = Codec.EncodePayload(payload),
    };

    private static SubmitOpPayload Submit(string type = "ChangeProperty", int idLength = 16) => new()
    {
        OpId = Guid.NewGuid().ToByteArray()[..Math.Min(16, idLength)].Concat(new byte[Math.Max(0, idLength - 16)]).ToArray(),
        Type = type,
        TargetId = Guid.NewGuid().ToByteArray(),
        PayloadJson = Encoding.UTF8.GetBytes("{\"s\":\"res://a.tscn\",\"k\":\"position\",\"v\":1}"),
        ClientOpRef = Guid.NewGuid().ToByteArray(),
    };

    private async Task<FakeSocket> ConnectAsync(WebSocketGateway gateway, bool subscribe = true)
    {
        var socket = new FakeSocket();
        _ = Task.Run(() => gateway.HandleAsync(socket, null, AccessGrant.AllProjects, _stop.Token));
        await socket.NextAsync(MessageType.Welcome);
        if (subscribe)
        {
            socket.Receive(Message(MessageType.Subscribe, new SubscribePayload { ProjectId = Project.ToByteArray(), BranchId = BranchId.ToByteArray() }));
            while ((await socket.NextAsync(MessageType.SyncState)) is var f && Codec.DecodePayload<SyncStatePayload>(f.Payload).HasMore) { }
        }
        return socket;
    }

    [Fact]
    public async Task An_operation_committed_while_subscribing_is_not_lost()
    {
        var (gateway, ops, _, branches) = Gateway();
        await ops.CommitBatchAsync([new OperationSubmission(Guid.NewGuid(), "ChangeProperty", Guid.NewGuid(), "{}", Guid.NewGuid(), 0)], BranchId, Guid.Empty, Guid.Empty, default);
        // The head is read (1), then someone commits before the session is a
        // subscriber, so the op is neither broadcast to it nor below that head.
        var first = true;
        branches.GetAsync(BranchId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var b = new Branch(BranchId, Project, "main", ops.Head(BranchId), null, null, DateTime.UtcNow);
            if (first)
            {
                first = false;
                ops.CommitBatchAsync([new OperationSubmission(Guid.NewGuid(), "ChangeProperty", Guid.NewGuid(), "{}", Guid.NewGuid(), 0)],
                    BranchId, Guid.Empty, Guid.Empty, default).Wait();
            }
            return b;
        });

        var socket = new FakeSocket();
        _ = Task.Run(() => gateway.HandleAsync(socket, null, AccessGrant.AllProjects, _stop.Token));
        await socket.NextAsync(MessageType.Welcome);
        socket.Receive(Message(MessageType.Subscribe, new SubscribePayload { ProjectId = Project.ToByteArray(), BranchId = BranchId.ToByteArray() }));
        var sync = Codec.DecodePayload<SyncStatePayload>((await socket.NextAsync(MessageType.SyncState)).Payload);

        sync.HeadSeq.Should().Be(2);
        sync.TailOps.Select(o => o.Seq).Should().Equal(1, 2);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public async Task A_wrong_length_id_is_refused_and_the_connection_stays(int length)
    {
        var (gateway, _, _, _) = Gateway();
        var socket = await ConnectAsync(gateway);

        socket.Receive(Message(MessageType.SubmitOp, Submit(idLength: length)));
        Codec.DecodePayload<ErrorPayload>((await socket.NextAsync(MessageType.Error)).Payload).Code.Should().Be(400);

        socket.Receive(Message(MessageType.Subscribe, new SubscribePayload { ProjectId = new byte[length], BranchId = BranchId.ToByteArray() }));
        Codec.DecodePayload<ErrorPayload>((await socket.NextAsync(MessageType.Error)).Payload).Code.Should().Be(400);

        socket.Receive(Message(MessageType.UndoRequest, new UndoRequestPayload { RequestId = new byte[length], Kind = "undo", Ops = [Submit()] }));
        Codec.DecodePayload<ErrorPayload>((await socket.NextAsync(MessageType.Error)).Payload).Code.Should().Be(400);

        socket.Receive(Message(MessageType.Ping, new PingPongPayload { Token = [7] }, Channel.System));
        (await socket.NextAsync(MessageType.Pong)).Should().NotBeNull();
        socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task A_database_failure_is_answered_with_try_again_and_the_connection_stays()
    {
        var (gateway, ops, _, _) = Gateway();
        var socket = await ConnectAsync(gateway);
        ops.BeforeCommit = () => throw new TimeoutException("database busy");

        socket.Receive(Message(MessageType.SubmitOp, Submit()));
        var error = Codec.DecodePayload<ErrorPayload>((await socket.NextAsync(MessageType.Error)).Payload);
        error.Retryable.Should().BeTrue();

        ops.BeforeCommit = null;
        var again = Submit();
        socket.Receive(Message(MessageType.SubmitOp, again));
        (await socket.NextAsync(MessageType.OpCommitted)).Should().NotBeNull();
        socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task Undo_and_ordinary_commits_are_broadcast_in_log_order()
    {
        var (gateway, ops, _, _) = Gateway();
        var writer = await ConnectAsync(gateway);
        var watcher = await ConnectAsync(gateway);
        ops.BeforeCommit = () => Task.Delay(Random.Shared.Next(3));

        // Ops to undo, by the same actor (the dev identity: no sign-in).
        var originals = Enumerable.Range(0, 10).Select(_ => Submit()).ToList();
        foreach (var o in originals) writer.Receive(Message(MessageType.SubmitOp, o));
        for (var i = 0; i < 10; i++) await watcher.NextAsync(MessageType.OpCommitted);

        // Undos from one connection and edits from another, at the same time.
        var editor = await ConnectAsync(gateway);
        for (var i = 0; i < 10; i++)
        {
            writer.Receive(Message(MessageType.UndoRequest, new UndoRequestPayload
            {
                RequestId = Guid.NewGuid().ToByteArray(),
                Kind = "undo",
                UndoOf = [originals[i].OpId],
                Ops = [Submit(), Submit(), Submit()],
            }));
            editor.Receive(Message(MessageType.SubmitOp, Submit()));
        }

        var seqs = new List<long>();
        for (var i = 0; i < 40; i++) seqs.Add((await watcher.NextAsync(MessageType.OpCommitted)).SeqOrAck);
        seqs.Should().Equal(Enumerable.Range(11, 40).Select(i => (long)i), "every broadcast arrives in log order, none missing");
    }
}
