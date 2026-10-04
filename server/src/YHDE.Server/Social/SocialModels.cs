namespace YHDE.Server.Social;

// Chat and comment records (social.md). Authors are member ids: the account
// for a signed-in editor, otherwise Hello.MemberId. Names are kept with what
// they wrote, so history still reads right after someone leaves or renames.

public sealed record ChatMessage(
    Guid MessageId,
    Guid ProjectId,
    Guid AuthorId,
    string AuthorName,
    Guid? RecipientId,
    string Body,
    DateTimeOffset CreatedAt);

public sealed record CommentMessage(
    Guid MessageId,
    Guid ThreadId,
    Guid AuthorId,
    string AuthorName,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt = null,
    DateTimeOffset? DeletedAt = null);

public sealed record CommentThread(
    Guid ThreadId,
    Guid ProjectId,
    Guid BranchId,
    string Scene,
    Guid? NodeId,
    string NodePath,
    string AnchorJson,
    Guid AuthorId,
    string AuthorName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    string? ResolvedByName,
    IReadOnlyList<CommentMessage> Messages);

public interface ISocialStore
{
    Task AddChatAsync(ChatMessage message, CancellationToken ct);

    // Newest first. `with` null = the project channel; otherwise the direct
    // messages between `member` and `with`.
    Task<IReadOnlyList<ChatMessage>> ChatPageAsync(
        Guid projectId, Guid member, Guid? with, DateTimeOffset? before, int limit, CancellationToken ct);

    // Newest first: direct messages `member` sent or received.
    Task<IReadOnlyList<ChatMessage>> RecentDirectAsync(Guid projectId, Guid member, int limit, CancellationToken ct);

    Task CreateThreadAsync(CommentThread thread, CommentMessage first, CancellationToken ct);
    Task<CommentThread?> GetThreadAsync(Guid threadId, CancellationToken ct);

    // Open threads, and threads resolved since `resolvedSince`, oldest first.
    Task<IReadOnlyList<CommentThread>> ThreadsAsync(Guid branchId, DateTimeOffset resolvedSince, int limit, CancellationToken ct);

    Task AddCommentAsync(CommentMessage message, CancellationToken ct);
    Task<CommentMessage?> GetCommentAsync(Guid messageId, CancellationToken ct);

    // Edits and removals keep the previous text in audit_log.
    Task EditCommentAsync(CommentMessage message, string body, DateTimeOffset at, Guid projectId, CancellationToken ct);
    Task DeleteCommentAsync(CommentMessage message, DateTimeOffset at, Guid projectId, CancellationToken ct);
    Task SetResolvedAsync(Guid threadId, DateTimeOffset? at, string? byName, CancellationToken ct);
}
