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
//
// An undo or redo is a batch: all-or-nothing, consecutive seqs. It goes
// through the same writer, so its broadcast keeps log order with everything
// else on the branch. Broadcasting only queues (SessionManager), so a slow
// client never holds up the writer.
public sealed class BranchCommitter(
    IOperationRepository operationRepo,
    ISessionManager sessionManager,
    ILogger<BranchCommitter> logger)
{
    public const int MaxGroup = 256;
    // A branch nobody wrote to for this long gives up its worker.
    private static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(10);

    private sealed record Batch(IReadOnlyList<OperationSubmission> Ops, Guid ActorId, Guid SessionId,
        TaskCompletionSource<IReadOnlyList<Operation>> Done);

    // One single operation or one batch.
    private sealed record Pending(CommitItem? Item, TaskCompletionSource<Operation?>? Done, Batch? Batch = null);

    private readonly ConcurrentDictionary<Guid, Channel<Pending>> _queues = new();
    private readonly Lock _gate = new();

    // The committed operation, or null when it was already in the log.
    public Task<Operation?> CommitAsync(OperationSubmission submission, Guid branchId, Guid actorId, Guid sessionId, CancellationToken ct)
    {
        var done = new TaskCompletionSource<Operation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(branchId, new Pending(new CommitItem(submission, actorId, sessionId), done));
        return done.Task.WaitAsync(ct);
    }

    // Commits `ops` together with consecutive seqs, or none of them.
    public Task<IReadOnlyList<Operation>> CommitBatchAsync(IReadOnlyList<OperationSubmission> ops, Guid branchId, Guid actorId, Guid sessionId,
        CancellationToken ct)
    {
        var done = new TaskCompletionSource<IReadOnlyList<Operation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(branchId, new Pending(null, null, new Batch(ops, actorId, sessionId, done)));
        return done.Task.WaitAsync(ct);
    }

    // Branches with a running writer (tests, admin).
    public int ActiveBranches => _queues.Count;

    private void Enqueue(Guid branchId, Pending pending)
    {
        lock (_gate)
        {
            var queue = _queues.GetOrAdd(branchId, id =>
            {
                var channel = Channel.CreateUnbounded<Pending>(new UnboundedChannelOptions { SingleReader = true });
                _ = Task.Run(() => SuperviseAsync(id, channel));
                return channel;
            });
            queue.Writer.TryWrite(pending);
        }
    }

    // Keeps the writer running: an unexpected error is logged and the loop
    // starts again, so a branch never stops committing silently.
    private async Task SuperviseAsync(Guid branchId, Channel<Pending> channel)
    {
        while (true)
        {
            try
            {
                await RunAsync(branchId, channel);
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The commit loop of branch {Branch} failed; restarting it", branchId);
                await Task.Delay(100);
            }
        }
    }

    private async Task RunAsync(Guid branchId, Channel<Pending> channel)
    {
        var reader = channel.Reader;
        var group = new List<Pending>(MaxGroup);
        while (true)
        {
            using (var idle = new CancellationTokenSource(IdleFor))
            {
                try
                {
                    if (!await reader.WaitToReadAsync(idle.Token)) return;
                }
                catch (OperationCanceledException)
                {
                    // Idle: retire this worker unless something arrived meanwhile.
                    lock (_gate)
                    {
                        if (reader.TryPeek(out _)) continue;
                        _queues.TryRemove(new KeyValuePair<Guid, Channel<Pending>>(branchId, channel));
                        channel.Writer.TryComplete();
                    }
                    return;
                }
            }

            group.Clear();
            Pending? batch = null;
            while (group.Count < MaxGroup && reader.TryPeek(out var next))
            {
                // A batch goes alone, after the singles queued before it.
                if (next.Batch is not null)
                {
                    if (group.Count == 0 && reader.TryRead(out var b)) batch = b;
                    break;
                }
                if (reader.TryRead(out var single)) group.Add(single);
            }
            if (batch is not null) await CommitBatchAsync(branchId, batch.Batch!);
            else if (group.Count > 0) await CommitGroupAsync(branchId, group);
        }
    }

    private async Task CommitGroupAsync(Guid branchId, List<Pending> group)
    {
        IReadOnlyList<Operation?> committed;
        try
        {
            committed = await operationRepo.CommitGroupAsync(group.Select(p => p.Item!).ToList(), branchId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to commit {Count} ops on branch {Branch}", group.Count, branchId);
            foreach (var p in group) p.Done!.TrySetException(ex);
            return;
        }

        // Broadcast only after the transaction committed, in seq order
        // (reliability.md).
        foreach (var op in committed)
        {
            if (op is not null) await BroadcastAsync(op);
        }
        if (group.Count > 1) logger.LogDebug("Committed a group of {Count} ops on branch {Branch}", group.Count, branchId);
        for (var i = 0; i < group.Count; i++) group[i].Done!.TrySetResult(i < committed.Count ? committed[i] : null);
    }

    private async Task CommitBatchAsync(Guid branchId, Batch batch)
    {
        IReadOnlyList<Operation> committed;
        try
        {
            committed = await operationRepo.CommitBatchAsync(batch.Ops, branchId, batch.ActorId, batch.SessionId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to commit a batch of {Count} ops on branch {Branch}", batch.Ops.Count, branchId);
            batch.Done.TrySetException(ex);
            return;
        }
        foreach (var op in committed) await BroadcastAsync(op);
        batch.Done.TrySetResult(committed);
    }

    private async Task BroadcastAsync(Operation op)
    {
        try
        {
            await sessionManager.BroadcastOpCommittedAsync(op, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Broadcast of seq {Seq} failed", op.Seq);
        }
    }
}
