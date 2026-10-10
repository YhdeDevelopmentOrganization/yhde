using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using YHDE.Server.Framing;
using YHDE.Server.Framing.Messages;
using YHDE.Server.Gateway;
using YHDE.Server.Presence;

namespace YHDE.Server.Tests.Presence;

// Unit tests for the ephemeral presence relay (presence.md).
public sealed class PresenceServiceTests
{
    private static readonly Guid BranchA = Guid.NewGuid();
    private static readonly Guid BranchB = Guid.NewGuid();

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Sent(Guid To, PresenceStatePayload Payload);

    private static (PresenceService, ISessionManager, List<Sent>, ManualTime) Build(params SessionState[] sessions)
    {
        var sent = new List<Sent>();
        var sm = Substitute.For<ISessionManager>();
        sm.GetBranchSubscribers(Arg.Any<Guid>())
            .Returns(ci => sessions.Where(s => s.SubscribedBranchId == ci.Arg<Guid>()).ToList());
        void Record(SessionState to, Frame frame)
        {
            frame.MsgType.Should().Be(MessageType.PresenceState);
            frame.Channel.Should().Be(Channel.Presence);
            lock (sent) sent.Add(new Sent(to.SessionId, Codec.DecodePayload<PresenceStatePayload>(frame.Payload)));
        }
        sm.SendFrameAsync(Arg.Any<SessionState>(), Arg.Any<Frame>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Record(ci.Arg<SessionState>(), ci.Arg<Frame>());
                return Task.CompletedTask;
            });
        // Fan-out encodes once and sends the same bytes to every peer.
        sm.SendEncodedAsync(Arg.Any<SessionState>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(ci =>
            {
                Record(ci.Arg<SessionState>(), Codec.Decode(ci.Arg<byte[]>().AsMemory(4)));
                return Task.CompletedTask;
            });
        var time = new ManualTime();
        return (new PresenceService(sm, time, NullLogger<PresenceService>.Instance), sm, sent, time);
    }

    private static SessionState Session(Guid? branch) => new() { SubscribedBranchId = branch };

    private static PresenceUpdatePayload Update(string name = "Ada", string scene = "res://main.tscn") => new()
    {
        DisplayName = name,
        Scene = scene,
        Tool = "2D",
        StateJson = """{"sel":[],"cur":[1,2]}""",
    };

    [Fact]
    public async Task Update_is_fanned_out_to_other_branch_subscribers_only()
    {
        var a = Session(BranchA);
        var b = Session(BranchA);
        var c = Session(BranchB);
        var (svc, _, sent, _) = Build(a, b, c);

        var entry = await svc.HandleUpdateAsync(a, Update(), default);

        entry.Should().NotBeNull();
        sent.Should().ContainSingle();
        sent[0].To.Should().Be(b.SessionId);
        sent[0].Payload.Full.Should().BeFalse();
        var e = sent[0].Payload.Entries.Should().ContainSingle().Subject;
        new Guid(e.SessionId).Should().Be(a.SessionId);
        e.DisplayName.Should().Be("Ada");
        e.Scene.Should().Be("res://main.tscn");
        e.StateJson.Should().Contain("cur");
    }

    [Fact]
    public async Task Unsubscribed_session_updates_are_dropped()
    {
        var a = Session(null);
        var (svc, _, sent, _) = Build(a);

        (await svc.HandleUpdateAsync(a, Update(), default)).Should().BeNull();
        sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Snapshot_contains_peers_but_not_self()
    {
        var a = Session(BranchA);
        var b = Session(BranchA);
        var (svc, _, sent, _) = Build(a, b);
        await svc.HandleUpdateAsync(a, Update("Ada"), default);
        await svc.HandleUpdateAsync(b, Update("Bo"), default);
        sent.Clear();

        await svc.SendSnapshotAsync(b, default);

        sent.Should().ContainSingle();
        sent[0].Payload.Full.Should().BeTrue();
        sent[0].Payload.Entries.Select(e => e.DisplayName).Should().Equal("Ada");
    }

    [Fact]
    public async Task Remove_broadcasts_left_to_former_peers()
    {
        var a = Session(BranchA);
        var b = Session(BranchA);
        var (svc, _, sent, _) = Build(a, b);
        await svc.HandleUpdateAsync(a, Update(), default);
        sent.Clear();

        await svc.RemoveAsync(a.SessionId, default);

        sent.Should().ContainSingle();
        sent[0].To.Should().Be(b.SessionId);
        sent[0].Payload.Left.Select(x => new Guid(x)).Should().Equal(a.SessionId);
        svc.Get(a.SessionId).Should().BeNull();
    }

    [Fact]
    public async Task Removing_unknown_session_is_silent()
    {
        var (svc, _, sent, _) = Build();
        await svc.RemoveAsync(Guid.NewGuid(), default);
        sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Rate_limit_drops_excess_updates_and_refills_over_time()
    {
        var a = Session(BranchA);
        var (svc, _, _, time) = Build(a);

        var accepted = 0;
        for (var i = 0; i < 100; i++)
            if (await svc.HandleUpdateAsync(a, Update(), default) is not null) accepted++;
        accepted.Should().Be((int)PresenceService.Burst);

        time.Now += TimeSpan.FromSeconds(1);
        (await svc.HandleUpdateAsync(a, Update(), default)).Should().NotBeNull();
    }

    [Fact]
    public async Task Hostile_input_is_sanitized()
    {
        var a = Session(BranchA);
        var (svc, _, _, _) = Build(a);

        var entry = await svc.HandleUpdateAsync(a, new PresenceUpdatePayload
        {
            DisplayName = "\u0007" + new string('x', 500),
            Scene = "/etc/passwd",
            Tool = new string('t', 500),
            StateJson = "[1,2,3]",
        }, default);

        entry!.DisplayName.Length.Should().Be(PresenceService.MaxDisplayNameLength);
        entry.DisplayName.Should().NotContain("\u0007");
        entry.Scene.Should().BeEmpty();
        entry.Tool.Length.Should().Be(PresenceService.MaxToolLength);
        entry.StateJson.Should().Be("{}");
    }

    [Theory]
    [InlineData(null, "Anonymous")]
    [InlineData("   ", "Anonymous")]
    [InlineData("  Ada  ", "Ada")]
    public void Names_are_trimmed_and_never_empty(string? input, string expected)
        => PresenceService.SanitizeName(input).Should().Be(expected);

    [Fact]
    public void Oversized_or_invalid_state_is_replaced()
    {
        PresenceService.SanitizeState(new string(' ', PresenceService.MaxStateBytes + 1)).Should().Be("{}");
        PresenceService.SanitizeState("{not json").Should().Be("{}");
        PresenceService.SanitizeState("""{"a":1}""").Should().Be("""{"a":1}""");
    }
}
