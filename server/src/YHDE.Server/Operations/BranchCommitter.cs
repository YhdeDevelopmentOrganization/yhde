using System.Collections.Concurrent;
using System.Threading.Channels;
using YHDE.Server.Domain;
using YHDE.Server.Gateway;
using YHDE.Server.Persistence.Repositories;

namespace YHDE.Server.Operations;

// Group commit per branch (operation_system.md, capacity.md).
//
// Every branch has one writer: submissions queue up and a single worker
// commits whatever is waiting in one transaction: one lock, one round trip,
// one disk flush for the whole group, then broadcasts the group in log order
// and only then answers the submitters. Under light load a group is one
// operation; under heavy load the cost per operation drops sharply instead of
// connections piling up on the branch lock.
public sealed class BranchCommitter(
    IOperationRepository operationRepo,
    ISessionManager sessionManager,
    ILogger<BranchCommitter> logger)
{
    public const int MaxGroup = 256;

    private sealed record Pending(CommitItem Item, TaskCompletionSource<Operation?> Done);

    private readonly ConcurrentDictionary<Guid, Channel<Pending>> _queues = new();

    // The committed operation, or null when it was already in the log.
    public Task<Operation?> CommitAsync(OperationSubmission submission, Guid branchId, Guid actorId, Guid sessionId, CancellationToken ct)
    {
        var queue = _queues.GetOrAdd(branchId, id =>
        {
            var channel = Channel.CreateUnbounded<Pending>(new UnboundedChannelOptions { SingleReader = true });
            _ = Task.Run(() => RunAsync(id, channel.Reader));
            return channel;
        });
        var pending = new Pending(new CommitItem(submission, actorId, sessionId),
            new TaskCompletionSource<Operation?>(TaskCreationOptions.RunContinuationsAsynchronously));
        queue.Writer.TryWrite(pending);
        return pending.Done.Task.WaitAsync(ct);
    }

    private async Task RunAsync(Guid branchId, ChannelReader<Pending> reader)
    {
        var group = new List<Pending>(MaxGroup);
        while (await reader.WaitToReadAsync())
        {
            group.Clear();
            while (group.Count < MaxGroup && reader.TryRead(out var next)) group.Add(next);

            IReadOnlyList<Operation?> committed;
            try
            {
                committed = await operationRepo.CommitGroupAsync(group.Select(p => p.Item).ToList(), branchId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to commit {Count} ops on branch {Branch}", group.Count, branchId);
                foreach (var p in group) p.Done.TrySetException(ex);
                continue;
            }

            // Broadcast only after the transaction committed, in seq order
            // (reliability.md).
            foreach (var op in committed)
            {
                if (op is null) continue;
                try
                {
                    await sessionManager.BroadcastOpCommittedAsync(op, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Broadcast of seq {Seq} failed", op.Seq);
                }
            }
            if (group.Count > 1) logger.LogDebug("Committed a group of {Count} ops on branch {Branch}", group.Count, branchId);
            for (var i = 0; i < group.Count; i++) group[i].Done.TrySetResult(committed[i]);
        }
    }
}
