namespace YHDE.Server.Domain;

// A project row. Named ProjectEntity so it doesn't clash with the Projects
// namespace.
public sealed record ProjectEntity(
    Guid ProjectId,
    string Name,
    DateTime CreatedAt
);
