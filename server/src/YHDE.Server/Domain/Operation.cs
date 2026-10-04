namespace YHDE.Server.Domain;

// Committed operation as it exists in the authoritative log.
// All server-stamped fields (Seq, ActorId, SessionId, CreatedAt, Signature) are
// assigned server-side and must never be taken from client input.
// See operation_system.md.
public sealed record Operation(
    Guid OpId,
    long Seq,
    Guid BranchId,
    string Type,
    Guid TargetId,
    string Payload,        // JSONB-compatible; validated to be a JSON object
    Guid ActorId,          // server-stamped
    Guid SessionId,        // server-stamped
    Guid ClientOpRef,
    long ParentSeq,
    DateTime CreatedAt,    // server-stamped (UTC)
    byte[] Signature       // hash-chain signature, server-computed
);

// The client-supplied portion of an operation submission.
// Only these fields arrive from the client; everything else is server-assigned.
public sealed record OperationSubmission(
    Guid OpId,
    string Type,
    Guid TargetId,
    string Payload,
    Guid ClientOpRef,
    long ParentSeq
);
