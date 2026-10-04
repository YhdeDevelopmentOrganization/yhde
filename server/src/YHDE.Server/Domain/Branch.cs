namespace YHDE.Server.Domain;

// A line of operation history in a project, with its own seq numbers
// (versioning.md).
public sealed record Branch(
    Guid BranchId,
    Guid ProjectId,
    string Name,
    long HeadSeq,
    Guid? BaseBranchId,
    long? BaseSeq,
    DateTime CreatedAt
);
