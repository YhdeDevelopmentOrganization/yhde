using System.Text.Json;
using Dapper;
using YHDE.Server.Persistence;

namespace YHDE.Server.Social;

// PostgreSQL storage for chat and comments (002_social.sql).
public sealed class SocialStore(Database db) : ISocialStore
{
    public async Task AddChatAsync(ChatMessage m, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO chat_messages (message_id, project_id, author_id, author_name, recipient_id, body, created_at)
            VALUES (@MessageId, @ProjectId, @AuthorId, @AuthorName, @RecipientId, @Body, @CreatedAt)
            """,
            new { m.MessageId, m.ProjectId, m.AuthorId, m.AuthorName, m.RecipientId, m.Body, CreatedAt = m.CreatedAt.UtcDateTime },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ChatMessage>> ChatPageAsync(
        Guid projectId, Guid member, Guid? with, DateTimeOffset? before, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var sql = with is null
            ? """
              SELECT message_id, project_id, author_id, author_name, recipient_id, body, created_at
              FROM chat_messages
              WHERE project_id = @projectId AND recipient_id IS NULL AND created_at < @before
              ORDER BY created_at DESC LIMIT @limit
              """
            : """
              SELECT message_id, project_id, author_id, author_name, recipient_id, body, created_at
              FROM chat_messages
              WHERE project_id = @projectId AND created_at < @before
                AND ((author_id = @member AND recipient_id = @with) OR (author_id = @with AND recipient_id = @member))
              ORDER BY created_at DESC LIMIT @limit
              """;
        var rows = await conn.QueryAsync<ChatRow>(new CommandDefinition(sql,
            new { projectId, member, with, before = (before ?? DateTimeOffset.MaxValue.AddDays(-1)).UtcDateTime, limit },
            cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<ChatMessage>> RecentDirectAsync(Guid projectId, Guid member, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ChatRow>(new CommandDefinition(
            """
            SELECT message_id, project_id, author_id, author_name, recipient_id, body, created_at
            FROM chat_messages
            WHERE project_id = @projectId AND recipient_id IS NOT NULL
              AND (author_id = @member OR recipient_id = @member)
            ORDER BY created_at DESC LIMIT @limit
            """,
            new { projectId, member, limit }, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task CreateThreadAsync(CommentThread t, CommentMessage first, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO comment_threads (thread_id, project_id, branch_id, scene, node_id, node_path, anchor,
                                         author_id, author_name, created_at)
            VALUES (@ThreadId, @ProjectId, @BranchId, @Scene, @NodeId, @NodePath, CAST(@AnchorJson AS JSONB),
                    @AuthorId, @AuthorName, @CreatedAt)
            """,
            new
            {
                t.ThreadId, t.ProjectId, t.BranchId, t.Scene, t.NodeId, t.NodePath, t.AnchorJson,
                t.AuthorId, t.AuthorName, CreatedAt = t.CreatedAt.UtcDateTime,
            },
            tx, cancellationToken: ct));
        await InsertCommentAsync(conn, tx, first, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<CommentThread?> GetThreadAsync(Guid threadId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ThreadRow>(new CommandDefinition(
            ThreadSelect + " WHERE thread_id = @threadId", new { threadId }, cancellationToken: ct));
        if (row is null) return null;
        var messages = await conn.QueryAsync<CommentRow>(new CommandDefinition(
            CommentSelect + " WHERE thread_id = @threadId ORDER BY created_at", new { threadId }, cancellationToken: ct));
        return row.ToModel(messages.Select(m => m.ToModel()).ToList());
    }

    public async Task<IReadOnlyList<CommentThread>> ThreadsAsync(
        Guid branchId, DateTimeOffset resolvedSince, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var threads = (await conn.QueryAsync<ThreadRow>(new CommandDefinition(
            ThreadSelect + """
             WHERE branch_id = @branchId AND (resolved_at IS NULL OR resolved_at >= @resolvedSince)
             ORDER BY created_at DESC LIMIT @limit
            """,
            new { branchId, resolvedSince = resolvedSince.UtcDateTime, limit }, cancellationToken: ct))).ToList();
        if (threads.Count == 0) return [];
        var ids = threads.Select(t => t.thread_id).ToArray();
        var messages = (await conn.QueryAsync<CommentRow>(new CommandDefinition(
            CommentSelect + " WHERE thread_id = ANY(@ids) ORDER BY created_at", new { ids }, cancellationToken: ct)))
            .GroupBy(m => m.thread_id)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CommentMessage>)g.Select(m => m.ToModel()).ToList());
        threads.Reverse(); // oldest first
        return threads.Select(t => t.ToModel(messages.GetValueOrDefault(t.thread_id, []))).ToList();
    }

    public async Task AddCommentAsync(CommentMessage m, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await InsertCommentAsync(conn, null, m, ct);
    }

    public async Task<CommentMessage?> GetCommentAsync(Guid messageId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<CommentRow>(new CommandDefinition(
            CommentSelect + " WHERE message_id = @messageId", new { messageId }, cancellationToken: ct));
        return row?.ToModel();
    }

    public Task EditCommentAsync(CommentMessage m, string body, DateTimeOffset at, Guid projectId, CancellationToken ct) =>
        ChangeCommentAsync(m, "comment.edit",
            "UPDATE comment_messages SET body = @body, edited_at = @at WHERE message_id = @id",
            new { body, at = at.UtcDateTime, id = m.MessageId }, projectId, ct);

    public Task DeleteCommentAsync(CommentMessage m, DateTimeOffset at, Guid projectId, CancellationToken ct) =>
        ChangeCommentAsync(m, "comment.delete",
            "UPDATE comment_messages SET deleted_at = @at WHERE message_id = @id",
            new { at = at.UtcDateTime, id = m.MessageId }, projectId, ct);

    public async Task SetResolvedAsync(Guid threadId, DateTimeOffset? at, string? byName, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE comment_threads SET resolved_at = @at, resolved_by_name = @byName WHERE thread_id = @threadId",
            new { at = at?.UtcDateTime, byName, threadId }, cancellationToken: ct));
    }

    // Helpers

    private async Task ChangeCommentAsync(CommentMessage m, string eventType, string sql, object args, Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit_log (event_type, actor_id, project_id, target_id, detail)
            VALUES (@eventType, @actor, @projectId, @target, CAST(@detail AS JSONB))
            """,
            new
            {
                eventType,
                actor = m.AuthorId,
                projectId,
                target = m.MessageId,
                detail = JsonSerializer.Serialize(new { thread = m.ThreadId, previous_body = m.Body }),
            },
            tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    private static Task InsertCommentAsync(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction? tx, CommentMessage m, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO comment_messages (message_id, thread_id, author_id, author_name, body, created_at)
            VALUES (@MessageId, @ThreadId, @AuthorId, @AuthorName, @Body, @CreatedAt)
            """,
            new { m.MessageId, m.ThreadId, m.AuthorId, m.AuthorName, m.Body, CreatedAt = m.CreatedAt.UtcDateTime },
            tx, cancellationToken: ct));

    private const string ThreadSelect =
        """
        SELECT thread_id, project_id, branch_id, scene, node_id, node_path, anchor::text AS anchor,
               author_id, author_name, created_at, resolved_at, resolved_by_name
        FROM comment_threads
        """;

    private const string CommentSelect =
        """
        SELECT message_id, thread_id, author_id, author_name, body, created_at, edited_at, deleted_at
        FROM comment_messages
        """;

    private static DateTimeOffset Utc(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc));

    // Rows as Dapper reads them (timestamptz arrives as a UTC DateTime).
#pragma warning disable IDE1006 // column names
    private sealed class ChatRow
    {
        public Guid message_id { get; init; }
        public Guid project_id { get; init; }
        public Guid author_id { get; init; }
        public string author_name { get; init; } = "";
        public Guid? recipient_id { get; init; }
        public string body { get; init; } = "";
        public DateTime created_at { get; init; }

        public ChatMessage ToModel() =>
            new(message_id, project_id, author_id, author_name, recipient_id, body, Utc(created_at));
    }

    private sealed class ThreadRow
    {
        public Guid thread_id { get; init; }
        public Guid project_id { get; init; }
        public Guid branch_id { get; init; }
        public string scene { get; init; } = "";
        public Guid? node_id { get; init; }
        public string node_path { get; init; } = "";
        public string anchor { get; init; } = "{}";
        public Guid author_id { get; init; }
        public string author_name { get; init; } = "";
        public DateTime created_at { get; init; }
        public DateTime? resolved_at { get; init; }
        public string? resolved_by_name { get; init; }

        public CommentThread ToModel(IReadOnlyList<CommentMessage> messages) =>
            new(thread_id, project_id, branch_id, scene, node_id, node_path, anchor, author_id, author_name,
                Utc(created_at), resolved_at is { } r ? Utc(r) : null, resolved_by_name, messages);
    }

    private sealed class CommentRow
    {
        public Guid message_id { get; init; }
        public Guid thread_id { get; init; }
        public Guid author_id { get; init; }
        public string author_name { get; init; } = "";
        public string body { get; init; } = "";
        public DateTime created_at { get; init; }
        public DateTime? edited_at { get; init; }
        public DateTime? deleted_at { get; init; }

        public CommentMessage ToModel() =>
            new(message_id, thread_id, author_id, author_name, body, Utc(created_at),
                edited_at is { } e ? Utc(e) : null, deleted_at is { } d ? Utc(d) : null);
    }
#pragma warning restore IDE1006
}
