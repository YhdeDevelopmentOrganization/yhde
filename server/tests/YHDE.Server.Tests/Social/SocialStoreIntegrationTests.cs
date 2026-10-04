using FluentAssertions;
using YHDE.Server.Social;

namespace YHDE.Server.Tests.Social;

// The SQL store against a real PostgreSQL. Runs when YHDE_TEST_DATABASE holds
// a connection string to a throwaway database; skipped otherwise.
[Collection("integration")]
public sealed class SocialStoreIntegrationTests
{
    private static readonly Guid Project = new("ffffffff-0000-0000-0000-000000000001");
    private static readonly Guid Branch = new("ffffffff-0000-0000-0000-000000000002");

    private static SocialStore? Open() => TestDatabase.Open() is { } db ? new SocialStore(db) : null;

    [Fact]
    public async Task Chat_pages_separate_the_channel_from_direct_messages()
    {
        var store = Open();
        if (store is null) return;
        var ada = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var cy = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await store.AddChatAsync(new ChatMessage(Guid.NewGuid(), Project, ada, "Ada", null, "hello all", t0), default);
        await store.AddChatAsync(new ChatMessage(Guid.NewGuid(), Project, ada, "Ada", bob, "hi bob", t0.AddSeconds(1)), default);
        await store.AddChatAsync(new ChatMessage(Guid.NewGuid(), Project, bob, "Bob", ada, "hi ada", t0.AddSeconds(2)), default);
        await store.AddChatAsync(new ChatMessage(Guid.NewGuid(), Project, cy, "Cy", ada, "not bob", t0.AddSeconds(3)), default);

        var dm = await store.ChatPageAsync(Project, ada, bob, null, 50, default);
        dm.Select(m => m.Body).Should().Equal("hi ada", "hi bob");
        var bobsDirect = await store.RecentDirectAsync(Project, bob, 50, default);
        bobsDirect.Should().HaveCount(2).And.OnlyContain(m => m.AuthorId == bob || m.RecipientId == bob);
        var channel = await store.ChatPageAsync(Project, cy, null, null, 500, default);
        channel.Should().Contain(m => m.Body == "hello all").And.OnlyContain(m => m.RecipientId == null);
        (await store.ChatPageAsync(Project, ada, bob, t0.AddSeconds(2), 50, default)).Select(m => m.Body).Should().Equal("hi bob");
    }

    [Fact]
    public async Task Threads_keep_their_messages_edits_and_removals()
    {
        var store = Open();
        if (store is null) return;
        var ada = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var thread = new CommentThread(Guid.NewGuid(), Project, Branch, "res://main.tscn", Guid.NewGuid(), "Player",
            """{"space":"2d","x":1,"y":2}""", ada, "Ada", now, null, null, []);
        var first = new CommentMessage(Guid.NewGuid(), thread.ThreadId, ada, "Ada", "first", now);
        await store.CreateThreadAsync(thread, first, default);
        await store.AddCommentAsync(new CommentMessage(Guid.NewGuid(), thread.ThreadId, ada, "Ada", "second", now.AddSeconds(1)), default);
        await store.EditCommentAsync(first, "first, edited", now.AddSeconds(2), Project, default);
        await store.SetResolvedAsync(thread.ThreadId, now.AddSeconds(3), "Bob", default);

        var loaded = await store.GetThreadAsync(thread.ThreadId, default);
        loaded!.Messages.Select(m => m.Body).Should().Equal("first, edited", "second");
        loaded.Messages[0].EditedAt.Should().NotBeNull();
        loaded.ResolvedByName.Should().Be("Bob");
        loaded.AnchorJson.Should().Contain("\"x\"");

        await store.DeleteCommentAsync(loaded.Messages[1], now.AddSeconds(4), Project, default);
        var listed = (await store.ThreadsAsync(Branch, now.AddDays(-1), 500, default)).Single(t => t.ThreadId == thread.ThreadId);
        listed.Messages[1].DeletedAt.Should().NotBeNull();
        (await store.ThreadsAsync(Branch, now.AddDays(1), 500, default)).Should().NotContain(t => t.ThreadId == thread.ThreadId);
    }
}
