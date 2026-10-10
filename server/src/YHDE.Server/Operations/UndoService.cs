using System.Text.Json.Nodes;
using YHDE.Server.Assets;
using YHDE.Server.Domain;
using YHDE.Server.Gateway;
using YHDE.Server.Persistence.Repositories;

namespace YHDE.Server.Operations;

// Server-authoritative undo and redo (operation_system.md).
//
// A request names the operations it undoes and carries their inverses, which
// the client computes from the prior state every operation records. The
// server decides: only the author may undo an operation, and the inverses are
// committed together (all-or-nothing, consecutive seqs), each annotated with
// "u": {"r": request, "k": "undo"|"redo"} (plus "of": [...] on the first) so
// the log shows what undid what. The originals are never edited: the log
// stays append-only. Redo is the undo of an undo.
//
// The batch goes through the branch's one writer (BranchCommitter), so it is
// broadcast in log order with everything else. File operations in it take
// the branch's text lock and refresh cached text, as single ones do.
public sealed class UndoService(
    IOperationRepository operationRepo,
    ISessionManager sessionManager,
    BlobStore blobs,
    ILogger<UndoService> logger,
    BranchCommitter? committer = null,
    Text.TextDocuments? texts = null)
{
    public const int MaxOperations = 20_000;
    // A refusal the editor answers by sending the request again later.
    public const string TryAgain = "TryAgain";

    private readonly BranchCommitter _committer = committer
        ?? new BranchCommitter(operationRepo, sessionManager, Microsoft.Extensions.Logging.Abstractions.NullLogger<BranchCommitter>.Instance);

    public sealed record Request(Guid RequestId, string Kind, IReadOnlyList<Guid> UndoOf, IReadOnlyList<OperationSubmission> Ops);

    public sealed record Result(string Status, string Code, string Reason, int Committed)
    {
        public static Result Refused(string code, string reason) => new("refused", code, reason, 0);
    }

    public async Task<Result> ProcessAsync(Request request, Guid branchId, Guid actorId, Guid sessionId, CancellationToken ct)
    {
        if (request.Kind is not ("undo" or "redo"))
            return Result.Refused("InvalidUndo", $"Unknown undo kind '{request.Kind}'.");
        if (request.Ops.Count == 0 || request.Ops.Count > MaxOperations)
            return Result.Refused("InvalidUndo", $"An undo carries 1..{MaxOperations} operations.");
        if (request.Ops.Select(o => o.OpId).Distinct().Count() != request.Ops.Count)
            return Result.Refused("InvalidUndo", "Operation ids in an undo must be unique.");

        // A resend after a reconnect: the request already went through.
        if (await operationRepo.ExistsAsync(request.Ops[0].OpId, ct))
            return new Result("committed", "Duplicate", "Already committed.", 0);

        foreach (var op in request.Ops)
        {
            // Text is undone in the editor, per person, as new edits (text_editing.md).
            if (op.Type == Domain.OperationType.EditText)
                return Result.Refused("InvalidUndo", "Text edits are undone as new edits, not through undo requests.");
            var error = OperationProcessor.Validate(op) ?? AssetRules.Validate(op, blobs);
            if (error is not null) return Result.Refused(error.Value.code.ToString(), error.Value.reason);
        }

        // Only the person who made a change can undo it.
        var originals = await operationRepo.GetByIdsAsync(branchId, request.UndoOf.Distinct().ToList(), ct);
        if (originals.Any(o => o.ActorId != actorId))
            return Result.Refused("NotAuthorized", "Only the author of a change can undo it.");

        var annotated = request.Ops
            .Select((op, i) => op with { Payload = Annotate(op.Payload, request, first: i == 0) })
            .ToList();

        var files = texts is not null && annotated.Any(o => AssetRules.IsAssetOp(o.Type));
        var gate = files ? texts!.LockFor(branchId) : null;
        if (gate is not null) await gate.WaitAsync(ct);
        IReadOnlyList<Operation> committed;
        try
        {
            // Committed and broadcast in log order by the branch's writer.
            committed = await _committer.CommitBatchAsync(annotated, branchId, actorId, sessionId, ct);
            if (files)
            {
                foreach (var op in annotated.Where(o => AssetRules.IsAssetOp(o.Type)))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(op.Payload);
                    var root = doc.RootElement;
                    texts!.Invalidate(branchId, root.TryGetProperty("s", out var s) ? s.GetString() : null);
                    texts.Invalidate(branchId, root.TryGetProperty("f", out var f) ? f.GetString() : null);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database may be busy for a moment: the editor sends it again.
            logger.LogError(ex, "Failed to commit {Kind} {RequestId}", request.Kind, request.RequestId);
            return Result.Refused(TryAgain, "The server could not save this right now. It will be tried again.");
        }
        finally
        {
            gate?.Release();
        }

        logger.LogInformation("{Kind} {RequestId} committed {Count} ops (seq {First}..{Last}) undoing {Undone} ops",
            request.Kind, request.RequestId, committed.Count, committed[0].Seq, committed[^1].Seq, originals.Count);

        return new Result("committed", "", "", committed.Count);
    }

    private static string Annotate(string payload, Request request, bool first)
    {
        var node = JsonNode.Parse(payload)!.AsObject();
        var u = new JsonObject
        {
            ["r"] = request.RequestId.ToString(),
            ["k"] = request.Kind,
        };
        if (first) u["of"] = new JsonArray(request.UndoOf.Select(id => (JsonNode)id.ToString()).ToArray());
        node["u"] = u;
        return node.ToJsonString();
    }
}
