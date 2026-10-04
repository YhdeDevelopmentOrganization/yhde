using YHDE.Server.Domain;

namespace YHDE.Server.Operations;

public enum RejectionCode
{
    InvalidType,
    InvalidPayload,
    InvalidTargetId,
    BranchNotFound,
    DuplicateOpId,
    ValidationFailed,
    AssetMissing,
    // The file already holds exactly this content (two editors registered the
    // same file, or both pulled the same commit). Nothing is committed; the
    // client treats it as done.
    AlreadyCurrent,
    // A text edit made against a version the server can no longer merge (the
    // file was replaced, or the edit is very old). The editor re-sends its
    // unsent typing against the current text.
    TextOutdated,
}

public abstract class SubmitResult
{
    public sealed class Committed(Operation operation) : SubmitResult
    {
        public Operation Operation { get; } = operation;
    }

    public sealed class Rejected(Guid clientOpRef, Guid opId, RejectionCode code, string reason) : SubmitResult
    {
        public Guid ClientOpRef { get; } = clientOpRef;
        public Guid OpId { get; } = opId;
        public RejectionCode Code { get; } = code;
        public string Reason { get; } = reason;
    }
}
