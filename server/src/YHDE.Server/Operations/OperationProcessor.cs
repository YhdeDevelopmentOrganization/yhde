using System.Text.Json;
using YHDE.Server.Assets;
using YHDE.Server.Domain;
using YHDE.Server.Gateway;
using YHDE.Server.Persistence.Repositories;
using YHDE.Server.Text;

namespace YHDE.Server.Operations;

// From a submitted operation to the log (operation_system.md):
//   1. check it: known type, JSON payload, target UUID, file bytes stored
//   2. commit it, dropping duplicates by op_id (BranchCommitter)
//   3. broadcast it to everyone in the branch
// Whether the sender may edit at all (view access) is checked by the gateway
// before it gets here.
//
// Broadcast happens only after the transaction commits (reliability.md). Each
// branch has one writer, BranchCommitter, backed by the FOR UPDATE lock in the
// repository.
public sealed class OperationProcessor(
    BranchCommitter committer,
    IOperationRepository operationRepo,
    BlobStore blobs,
    ILogger<OperationProcessor> logger,
    TextDocuments? texts = null)
{
    public async Task<SubmitResult> ProcessAsync(
        OperationSubmission submission,
        Guid branchId,
        Guid actorId,
        Guid sessionId,
        CancellationToken ct)
    {
        // 1. Validate.
        var validationError = Validate(submission) ?? AssetRules.Validate(submission, blobs);
        if (validationError is not null)
        {
            logger.LogWarning("Op {OpId} rejected: {Reason}", submission.OpId, validationError.Value.reason);
            return new SubmitResult.Rejected(
                submission.ClientOpRef, submission.OpId,
                validationError.Value.code,
                validationError.Value.reason);
        }

        // Files and live text of a branch commit one at a time (text_editing.md).
        var serialize = texts is not null && (AssetRules.IsAssetOp(submission.Type) || submission.Type == OperationType.EditText);
        if (!serialize) return await CommitAsync(submission, branchId, actorId, sessionId, null, ct);
        var gate = texts!.LockFor(branchId);
        await gate.WaitAsync(ct);
        try
        {
            TextDocuments.Prepared? text = null;
            if (submission.Type == OperationType.EditText)
            {
                var (ready, code, reason) = await texts.PrepareAsync(submission, branchId, ct);
                if (ready is null)
                {
                    logger.LogDebug("Edit {OpId} rejected: {Reason}", submission.OpId, reason);
                    return new SubmitResult.Rejected(submission.ClientOpRef, submission.OpId, code, reason);
                }
                text = ready;
                submission = ready.Submission;
            }
            return await CommitAsync(submission, branchId, actorId, sessionId, text, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SubmitResult> CommitAsync(
        OperationSubmission submission, Guid branchId, Guid actorId, Guid sessionId,
        TextDocuments.Prepared? text, CancellationToken ct)
    {
        // 2b. A file op that changes nothing is not logged (assets.md).
        if (AssetRules.IsAssetOp(submission.Type) && await IsAlreadyCurrentAsync(submission, branchId, ct))
        {
            logger.LogDebug("Op {OpId} ({Type}) changes nothing, not logged", submission.OpId, submission.Type);
            return new SubmitResult.Rejected(
                submission.ClientOpRef, submission.OpId,
                RejectionCode.AlreadyCurrent,
                "The file already has this content.");
        }

        // 2c. A new name that differs from a file on the branch only in
        // capitals: on Windows and macOS both are the same file, and editors
        // there would overwrite each other's.
        if (submission.Type is OperationType.RegisterAsset or OperationType.MoveAsset && PathOf(submission) is { } path
            && (await operationRepo.LivePathsDifferingInCaseAsync(branchId, path, ct)) is [var other, ..])
        {
            return new SubmitResult.Rejected(
                submission.ClientOpRef, submission.OpId,
                RejectionCode.InvalidPayload,
                $"{other} already exists. {path} differs from it only in capitals, which Windows and macOS treat as the same file: rename one of them.");
        }

        // 3. Commit (durable, one writer per branch, grouped under load)
        // and broadcast after the commit, both inside the committer. A resend
        // of an op already in the log is acknowledged, not committed twice.
        var committed = await committer.CommitAsync(submission, branchId, actorId, sessionId, ct);
        if (committed is null)
        {
            logger.LogDebug("Op {OpId} is a duplicate: ignored", submission.OpId);
            return new SubmitResult.Rejected(
                submission.ClientOpRef, submission.OpId,
                RejectionCode.DuplicateOpId,
                "Operation already committed (idempotent duplicate).");
        }

        logger.LogDebug(
            "Op committed: branch={Branch} seq={Seq} type={Type} actor={Actor}",
            branchId, committed.Seq, committed.Type, actorId);

        if (text is not null)
        {
            texts!.Applied(branchId, text, committed.Seq);
        }
        else if (texts is not null && AssetRules.IsAssetOp(committed.Type))
        {
            // New bytes for a file: its live text starts over from them.
            using var payload = JsonDocument.Parse(committed.Payload);
            var root = payload.RootElement;
            texts.Invalidate(branchId, root.TryGetProperty("s", out var s) ? s.GetString() : null);
            texts.Invalidate(branchId, root.TryGetProperty("f", out var f) ? f.GetString() : null);
        }

        // The broadcast already happened inside the committer, after the
        // transaction committed (reliability.md). The branch's text gate
        // is still held here, so the next edit sees this one applied.

        return new SubmitResult.Committed(committed);
    }

    // Two editors that start from the same files register each of them; the
    // second registration (same path, same bytes) would only repeat the log.
    // A best-effort check outside the branch lock: a rare concurrent duplicate
    // is still correct, just redundant.
    private static string? PathOf(OperationSubmission s)
    {
        using var doc = JsonDocument.Parse(s.Payload);
        return doc.RootElement.TryGetProperty("s", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    private async Task<bool> IsAlreadyCurrentAsync(OperationSubmission s, Guid branchId, CancellationToken ct)
    {
        if (s.Type == OperationType.MoveAsset) return false;
        var latest = await operationRepo.GetLatestAssetOpAsync(branchId, s.TargetId, ct);
        if (s.Type == OperationType.DeleteAsset)
            return latest is null || latest.Type == OperationType.DeleteAsset;
        if (latest is null || latest.Type == OperationType.DeleteAsset) return false;
        using var mine = JsonDocument.Parse(s.Payload);
        using var theirs = JsonDocument.Parse(latest.Payload);
        return mine.RootElement.TryGetProperty("h", out var h) && theirs.RootElement.TryGetProperty("h", out var current)
            && h.ValueKind == JsonValueKind.String && h.GetString() == current.GetString();
    }

    internal static (RejectionCode code, string reason)? Validate(OperationSubmission s)
    {
        if (!OperationType.IsKnown(s.Type))
            return (RejectionCode.InvalidType, $"Unknown operation type '{s.Type}'.");

        if (s.TargetId == Guid.Empty)
            return (RejectionCode.InvalidTargetId, "target_id must not be the empty UUID.");

        if (string.IsNullOrWhiteSpace(s.Payload))
            return (RejectionCode.InvalidPayload, "Payload must not be empty.");

        try
        {
            using var doc = JsonDocument.Parse(s.Payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (RejectionCode.InvalidPayload, "Payload must be a JSON object.");
        }
        catch (JsonException ex)
        {
            return (RejectionCode.InvalidPayload, $"Payload is not valid JSON: {ex.Message}");
        }

        return null;
    }
}
