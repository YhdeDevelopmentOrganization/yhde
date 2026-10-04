using MessagePack;

namespace YHDE.Server.Framing.Messages;

// Ops channel messages (network_protocol.md, Channel: ops).

[MessagePackObject]
public sealed class SubscribePayload
{
    [Key(0)] public byte[] ProjectId { get; set; } = [];  // 16-byte UUID
    [Key(1)] public byte[] BranchId { get; set; } = [];   // 16-byte UUID
    [Key(2)] public long LastAckedSeq { get; init; }       // client's known watermark; 0 = fresh
}

// SyncState: the server's reply to Subscribe, in pages: the operations the
// client is missing. The snapshot fields are reserved; there are no snapshots
// yet (snapshots.md), so SnapshotSeq is 0.
[MessagePackObject]
public sealed class SyncStatePayload
{
    [Key(0)] public byte[] BranchId { get; set; } = [];
    [Key(1)] public long SnapshotSeq { get; init; }        // 0 = no snapshot; client starts from seq 0
    [Key(2)] public byte[]? SnapshotRef { get; init; }     // reserved for snapshots; null
    [Key(3)] public CommittedOpPayload[] TailOps { get; set; } = [];
    [Key(4)] public long HeadSeq { get; init; }
    // Large catch-ups are paged; every page but the last has HasMore = true.
    [Key(5)] public bool HasMore { get; init; }
}

// SubmitOp: client proposes an operation (network_protocol.md).
// actor_id and session_id are NOT in this payload: they are server-stamped from the session.
[MessagePackObject]
public sealed class SubmitOpPayload
{
    [Key(0)] public byte[] OpId { get; set; } = [];       // 16-byte UUID
    [Key(1)] public string Type { get; set; } = "";
    [Key(2)] public byte[] TargetId { get; set; } = [];   // 16-byte UUID
    [Key(3)] public byte[] PayloadJson { get; set; } = []; // UTF-8 JSON bytes
    [Key(4)] public byte[] ClientOpRef { get; set; } = []; // 16-byte UUID
    [Key(5)] public long ParentSeq { get; init; }
}

// OpCommitted: broadcast to all subscribers after a durable commit.
// This is the authoritative record clients apply to their projected state.
[MessagePackObject]
public sealed class CommittedOpPayload
{
    [Key(0)] public byte[] OpId { get; set; } = [];
    [Key(1)] public long Seq { get; init; }
    [Key(2)] public byte[] BranchId { get; set; } = [];
    [Key(3)] public string Type { get; set; } = "";
    [Key(4)] public byte[] TargetId { get; set; } = [];
    [Key(5)] public byte[] PayloadJson { get; set; } = [];
    [Key(6)] public byte[] ActorId { get; set; } = [];
    [Key(7)] public byte[] SessionId { get; set; } = [];
    [Key(8)] public byte[] ClientOpRef { get; set; } = [];
    [Key(9)] public long ParentSeq { get; init; }
    [Key(10)] public long CreatedAtUnixMs { get; init; }
    [Key(11)] public byte[] Signature { get; set; } = [];
}

// OpRejected: sent only to the originating session.
[MessagePackObject]
public sealed class OpRejectedPayload
{
    [Key(0)] public byte[] ClientOpRef { get; set; } = [];
    [Key(1)] public byte[] OpId { get; set; } = [];
    [Key(2)] public string Reason { get; set; } = "";
    [Key(3)] public string Code { get; set; } = "";        // machine-readable error code
}

// Ack: watermark from client acknowledging received ops up to (and including) Seq.
[MessagePackObject]
public sealed class AckPayload
{
    [Key(0)] public long Seq { get; init; }
}

// Undo/redo of one editor action (operation_system.md): the inverse
// operations of the action, committed atomically and linked to the operations
// they undo. Redo is the undo of an undo.
[MessagePackObject]
public sealed class UndoRequestPayload
{
    [Key(0)] public byte[] RequestId { get; set; } = [];   // 16-byte UUID
    [Key(1)] public string Kind { get; set; } = "";        // "undo" | "redo"
    [Key(2)] public byte[][] UndoOf { get; set; } = [];    // op ids being undone
    [Key(3)] public SubmitOpPayload[] Ops { get; set; } = [];
}

// Server -> requester once the request is decided (after the broadcast).
[MessagePackObject]
public sealed class UndoResultPayload
{
    [Key(0)] public byte[] RequestId { get; set; } = [];
    [Key(1)] public string Status { get; set; } = "";      // "committed" | "refused"
    [Key(2)] public string Reason { get; set; } = "";
    [Key(3)] public string Code { get; set; } = "";
}
