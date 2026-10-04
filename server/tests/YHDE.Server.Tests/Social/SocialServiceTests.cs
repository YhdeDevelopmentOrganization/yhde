using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using YHDE.Server.Framing;
using YHDE.Server.Framing.Messages;
using YHDE.Server.Gateway;
using YHDE.Server.Social;

namespace YHDE.Server.Tests.Social;

// Chat and comments (social.md): who receives what, and what is refused.
public sealed class SocialServiceTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid BranchA = Guid.NewGuid();
    private static readonly Guid BranchB = Guid.NewGuid();

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Sent(SessionState To, string Kind, JsonElement Body, byte[] RequestId);

    private sealed class Harness
    {
        public required SocialService Service { get; init; }
        public required InMemorySocialStore Store { get; init; }
        public required List<Sent> Sent { get; init; }
        public required ManualTime Time { get; init; }

        public IEnumerable<Sent> To(SessionState s) => Sent.Where(x => x.To == s);

        public Task Request(SessionState s, string kind, object body, byte[]? id = null) =>
            Service.HandleAsync(s, new SocialRequestPayload
            {
                RequestId = id ?? Guid.NewGuid().ToByteArray(),
                Kind = kind,
                BodyJson = JsonSerializer.SerializeToUtf8Bytes(body),
            }, default);
    }

    private static Harness Build(params SessionState[] sessions)
    {
        var sent = new List<Sent>();
        var sm = Substitute.For<ISessionManager>();
        sm.GetBranchSubscribers(Arg.Any<Guid>())
            .Returns(ci => sessions.Where(s => s.SubscribedBranchId == ci.Arg<Guid>()).ToList());
        sm.GetProjectSessions(Arg.Any<Guid>())
            .Returns(ci => sessions.Where(s => s.SubscribedProjectId == ci.Arg<Guid>()).ToList());
        sm.SendFrameAsync(Arg.Any<SessionState>(), Arg.Any<Frame>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var frame = ci.Arg<Frame>();
                frame.MsgType.Should().Be(MessageType.SocialEvent);
                frame.Channel.Should().Be(Channel.Social);
                var e = Codec.DecodePayload<SocialEventPayload>(frame.Payload);
                using var doc = JsonDocument.Parse(e.BodyJson);
                lock (sent) sent.Add(new Sent(ci.Arg<SessionState>(), e.Kind, doc.RootElement.Clone(), e.RequestId));
                return Task.CompletedTask;
            });
        var store = new InMemorySocialStore();
        var time = new ManualTime();
        return new Harness
        {
            Service = new SocialService(sm, store, time, NullLogger<SocialService>.Instance),
            Store = store,
            Sent = sent,
            Time = time,
        };
    }

    private static SessionState Member(string name, Guid? branch = null, Guid? member = null) => new()
    {
        SubscribedProjectId = Project,
        SubscribedBranchId = branch ?? BranchA,
        MemberId = member ?? Guid.NewGuid(),
        MemberName = name,
    };

    [Fact]
    public async Task Project_chat_reaches_everyone_in_the_project_and_echoes_the_request_id()
    {
        var ada = Member("Ada");
        var bob = Member("Bob");
        var cy = Member("Cy", BranchB); // another branch, same project
        var h = Build(ada, bob, cy);
        var request = Guid.NewGuid().ToByteArray();

        await h.Request(ada, "chat.send", new { body = "  hello team \u0007 " }, request);

        h.Sent.Should().HaveCount(3).And.OnlyContain(s => s.Kind == "chat.message");
        h.To(ada).Single().RequestId.Should().Equal(request);
        h.To(bob).Single().RequestId.Should().BeEmpty();
        var body = h.To(cy).Single().Body;
        body.GetProperty("body").GetString().Should().Be("hello team");
        body.GetProperty("from").GetProperty("name").GetString().Should().Be("Ada");
        body.GetProperty("to").ValueKind.Should().Be(JsonValueKind.Null);
        h.Store.Chat.Should().ContainSingle();
    }

    [Fact]
    public async Task Direct_messages_reach_only_the_two_people_on_every_session_they_have()
    {
        var ada = Member("Ada");
        var bob = Member("Bob");
        var bobLaptop = Member("Bob", member: bob.MemberId);
        var cy = Member("Cy");
        var h = Build(ada, bob, bobLaptop, cy);

        await h.Request(ada, "chat.send", new { body = "psst", to = bob.MemberId });

        h.Sent.Select(s => s.To).Should().BeEquivalentTo([ada, bob, bobLaptop]);
        h.To(bob).Single().Body.GetProperty("to").GetGuid().Should().Be(bob.MemberId);
    }

    [Fact]
    public async Task Bad_chat_requests_get_an_error_only_for_the_sender()
    {
        var ada = Member("Ada");
        var bob = Member("Bob");
        var h = Build(ada, bob);

        await h.Request(ada, "chat.send", new { body = "   " });
        await h.Request(ada, "chat.send", new { body = new string('x', 4001) });
        await h.Request(ada, "chat.send", new { body = "me", to = ada.MemberId });
        await h.Request(ada, "chat.fly", new { body = "?" });

        h.Sent.Should().HaveCount(4).And.OnlyContain(s => s.To == ada && s.Kind == "error");
        h.Store.Chat.Should().BeEmpty();
    }

    [Fact]
    public async Task Requests_before_subscribing_are_refused()
    {
        var lurker = new SessionState { MemberId = Guid.NewGuid() };
        var h = Build(lurker);

        await h.Request(lurker, "chat.send", new { body = "hi" });

        h.Sent.Should().ContainSingle(s => s.Kind == "error");
        h.Store.Chat.Should().BeEmpty();
    }

    [Fact]
    public async Task Comments_are_pinned_per_branch_and_replies_reopen_resolved_threads()
    {
        var ada = Member("Ada");
        var bob = Member("Bob");
        var other = Member("Cy", BranchB);
        var h = Build(ada, bob, other);
        var node = Guid.NewGuid();

        await h.Request(ada, "comment.create", new
        {
            scene = "res://main.tscn",
            node,
            path = "Player/Sprite",
            anchor = new { space = "2d", x = 12.5, y = -3, extra = "dropped" },
            body = "Too bright?",
        });

        h.To(other).Should().BeEmpty();
        var thread = h.To(bob).Single().Body;
        thread.GetProperty("scene").GetString().Should().Be("res://main.tscn");
        thread.GetProperty("node").GetGuid().Should().Be(node);
        thread.GetProperty("anchor").GetRawText().Should().NotContain("extra");
        thread.GetProperty("messages").GetArrayLength().Should().Be(1);
        var id = thread.GetProperty("id").GetGuid();

        await h.Request(bob, "comment.resolve", new { thread = id, resolved = true });
        h.Store.Threads[id].ResolvedByName.Should().Be("Bob");

        await h.Request(ada, "comment.reply", new { thread = id, body = "Not fixed yet" });
        h.Store.Threads[id].ResolvedAt.Should().BeNull();
        h.Sent.Last().Body.GetProperty("messages").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Only_the_author_edits_or_removes_a_comment_and_the_old_text_is_kept()
    {
        var ada = Member("Ada");
        var bob = Member("Bob");
        var h = Build(ada, bob);
        await h.Request(ada, "comment.create", new { scene = "res://a.tscn", anchor = new { space = "3d", x = 1, y = 2, z = 3 }, body = "first" });
        var message = h.Store.Comments.Values.Single();

        await h.Request(bob, "comment.edit", new { message = message.MessageId, body = "hijacked" });
        h.Sent.Last().Kind.Should().Be("error");
        h.Sent.Last().To.Should().Be(bob);

        await h.Request(ada, "comment.edit", new { message = message.MessageId, body = "second" });
        h.Store.Comments[message.MessageId].Body.Should().Be("second");
        h.Store.Audit.Should().ContainSingle(a => a.Contains("first"));

        await h.Request(ada, "comment.delete", new { message = message.MessageId });
        h.Sent.Last().Body.GetProperty("messages").GetArrayLength().Should().Be(0);
        h.Store.Comments[message.MessageId].DeletedAt.Should().NotBeNull(); // kept, hidden
    }

    [Fact]
    public async Task Comments_need_a_scene_and_a_finite_position()
    {
        var ada = Member("Ada");
        var h = Build(ada);

        await h.Request(ada, "comment.create", new { scene = "user://x.tscn", anchor = new { space = "2d", x = 0, y = 0 }, body = "a" });
        await h.Request(ada, "comment.create", new { scene = "res://a.tscn", anchor = new { space = "4d", x = 0, y = 0 }, body = "a" });
        await h.Request(ada, "comment.create", new { scene = "res://a.tscn", anchor = new { space = "3d", x = 0, y = 0 }, body = "a" });
        await h.Request(ada, "comment.create", new { scene = "res://a.tscn", anchor = new { space = "2d", x = 1e12, y = 0 }, body = "a" });

        h.Sent.Should().HaveCount(4).And.OnlyContain(s => s.Kind == "error");
        h.Store.Threads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_thread_of_another_branch_cannot_be_touched()
    {
        var ada = Member("Ada");
        var cy = Member("Cy", BranchB);
        var h = Build(ada, cy);
        await h.Request(ada, "comment.create", new { scene = "res://a.tscn", anchor = new { space = "2d", x = 0, y = 0 }, body = "a" });
        var id = h.Store.Threads.Keys.Single();

        await h.Request(cy, "comment.resolve", new { thread = id });

        h.Store.Threads[id].ResolvedAt.Should().BeNull();
        h.To(cy).Single().Kind.Should().Be("error");
    }

    [Fact]
    public async Task Joining_sends_recent_chat_including_own_direct_messages_and_the_branch_comments()
    {
        var ada = Member("Ada");
        var bob = Member("Bob");
        var cy = Member("Cy");
        var h = Build(ada, bob, cy);
        await h.Request(ada, "chat.send", new { body = "all" });
        await h.Request(ada, "chat.send", new { body = "for bob", to = bob.MemberId });
        await h.Request(ada, "comment.create", new { scene = "res://a.tscn", anchor = new { space = "2d", x = 0, y = 0 }, body = "c" });
        h.Sent.Clear();

        await h.Service.SendInitialAsync(cy, default);
        await h.Service.SendInitialAsync(bob, default);

        var cyChat = h.To(cy).Single(s => s.Kind == "chat.recent").Body.GetProperty("messages");
        cyChat.EnumerateArray().Select(m => m.GetProperty("body").GetString()).Should().Equal("all");
        var bobChat = h.To(bob).Single(s => s.Kind == "chat.recent").Body.GetProperty("messages");
        bobChat.EnumerateArray().Select(m => m.GetProperty("body").GetString()).Should().Equal("all", "for bob");
        h.To(bob).Single(s => s.Kind == "comment.threads").Body.GetProperty("threads").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Floods_are_rate_limited()
    {
        var ada = Member("Ada");
        var h = Build(ada);

        for (var i = 0; i < 40; i++) await h.Request(ada, "chat.send", new { body = $"spam {i}" });

        h.Store.Chat.Count.Should().BeLessThan(40).And.BeGreaterThan(0);
        h.Sent.Should().Contain(s => s.Kind == "error");
    }
}

// Test double for ISocialStore with the same semantics as the SQL store.
public sealed class InMemorySocialStore : ISocialStore
{
    public List<ChatMessage> Chat { get; } = [];
    public Dictionary<Guid, CommentThread> Threads { get; } = [];
    public Dictionary<Guid, CommentMessage> Comments { get; } = [];
    public List<string> Audit { get; } = [];

    public Task AddChatAsync(ChatMessage message, CancellationToken ct)
    {
        Chat.Add(message);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> ChatPageAsync(Guid projectId, Guid member, Guid? with, DateTimeOffset? before, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ChatMessage>>(Chat
            .Where(m => m.ProjectId == projectId && (before is null || m.CreatedAt < before))
            .Where(m => with is null
                ? m.RecipientId is null
                : (m.AuthorId == member && m.RecipientId == with) || (m.AuthorId == with && m.RecipientId == member))
            .OrderByDescending(m => m.CreatedAt).Take(limit).ToList());

    public Task<IReadOnlyList<ChatMessage>> RecentDirectAsync(Guid projectId, Guid member, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ChatMessage>>(Chat
            .Where(m => m.ProjectId == projectId && m.RecipientId is not null && (m.AuthorId == member || m.RecipientId == member))
            .OrderByDescending(m => m.CreatedAt).Take(limit).ToList());

    public Task CreateThreadAsync(CommentThread thread, CommentMessage first, CancellationToken ct)
    {
        Threads[thread.ThreadId] = thread;
        Comments[first.MessageId] = first;
        return Task.CompletedTask;
    }

    public Task<CommentThread?> GetThreadAsync(Guid threadId, CancellationToken ct) =>
        Task.FromResult(Threads.TryGetValue(threadId, out var t)
            ? t with { Messages = Comments.Values.Where(c => c.ThreadId == threadId).OrderBy(c => c.CreatedAt).ToList() }
            : null);

    public async Task<IReadOnlyList<CommentThread>> ThreadsAsync(Guid branchId, DateTimeOffset resolvedSince, int limit, CancellationToken ct)
    {
        var list = new List<CommentThread>();
        foreach (var t in Threads.Values.Where(t => t.BranchId == branchId && (t.ResolvedAt is null || t.ResolvedAt >= resolvedSince)))
            list.Add((await GetThreadAsync(t.ThreadId, ct))!);
        return list;
    }

    public Task AddCommentAsync(CommentMessage message, CancellationToken ct)
    {
        Comments[message.MessageId] = message;
        return Task.CompletedTask;
    }

    public Task<CommentMessage?> GetCommentAsync(Guid messageId, CancellationToken ct) =>
        Task.FromResult(Comments.GetValueOrDefault(messageId));

    public Task EditCommentAsync(CommentMessage message, string body, DateTimeOffset at, Guid projectId, CancellationToken ct)
    {
        Audit.Add($"edit {message.MessageId}: {message.Body}");
        Comments[message.MessageId] = message with { Body = body, EditedAt = at };
        return Task.CompletedTask;
    }

    public Task DeleteCommentAsync(CommentMessage message, DateTimeOffset at, Guid projectId, CancellationToken ct)
    {
        Audit.Add($"delete {message.MessageId}: {message.Body}");
        Comments[message.MessageId] = message with { DeletedAt = at };
        return Task.CompletedTask;
    }

    public Task SetResolvedAsync(Guid threadId, DateTimeOffset? at, string? byName, CancellationToken ct)
    {
        Threads[threadId] = Threads[threadId] with { ResolvedAt = at, ResolvedByName = byName };
        return Task.CompletedTask;
    }
}
