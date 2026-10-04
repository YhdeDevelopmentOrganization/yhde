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
public sealed class UndoService(
    IOperationRepository operationRepo,
    ISessionManager sessionManager,
    BlobStore blobs,
    ILogger<UndoService> logger)
{
    public const int MaxOperations = 20_000;

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

        IReadOnlyList<Operation> committed;
        try
        {
            committed = await operationRepo.CommitBatchAsync(annotated, branchId, actorId, sessionId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to commit {Kind} {RequestId}", request.Kind, request.RequestId);
            throw;
        }

        logger.LogInformation("{Kind} {RequestId} committed {Count} ops (seq {First}..{Last}) undoing {Undone} ops",
            request.Kind, request.RequestId, committed.Count, committed[0].Seq, committed[^1].Seq, originals.Count);

        foreach (var op in committed)
            await sessionManager.BroadcastOpCommittedAsync(op, ct);

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
