using YHDE.Server.Domain;

namespace YHDE.Server.Persistence.Repositories;

public sealed record CommitItem(OperationSubmission Submission, Guid ActorId, Guid SessionId);

public interface IOperationRepository
{
    Task<Operation> CommitAsync(
        OperationSubmission submission, Guid branchId,
        Guid actorId, Guid sessionId, CancellationToken ct);

    Task<IReadOnlyList<Operation>> GetTailAsync(
        Guid branchId, long fromSeqExclusive, long toSeqInclusive, CancellationToken ct);

    Task<bool> ExistsAsync(Guid opId, CancellationToken ct);

    // Commits several operations in one transaction with consecutive seqs
    // (an undo/redo is all-or-nothing).
    Task<IReadOnlyList<Operation>> CommitBatchAsync(
        IReadOnlyList<OperationSubmission> submissions, Guid branchId,
        Guid actorId, Guid sessionId, CancellationToken ct);

    // Group commit: operations from several editors in one transaction, in
    // order. Returns null for an operation already in the log (a resend).
    Task<IReadOnlyList<Operation?>> CommitGroupAsync(
        IReadOnlyList<CommitItem> items, Guid branchId, CancellationToken ct);

    Task<IReadOnlyList<Operation>> GetByIdsAsync(Guid branchId, IReadOnlyCollection<Guid> opIds, CancellationToken ct);

    // The newest asset operation (Register/Update/Move/Delete) on a file's target id, if any.
    Task<Operation?> GetLatestAssetOpAsync(Guid branchId, Guid targetId, CancellationToken ct);

    // Operations of one type on one target after a seq, in log order.
    Task<IReadOnlyList<Operation>> GetTargetOpsAsync(Guid branchId, Guid targetId, long afterSeq, string type, CancellationToken ct);
}
