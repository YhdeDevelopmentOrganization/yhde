using YHDE.Server.Domain;

namespace YHDE.Server.Persistence.Repositories;

public interface IBranchRepository
{
    Task<Branch?> GetAsync(Guid branchId, CancellationToken ct);
    Task<Branch?> GetByNameAsync(Guid projectId, string name, CancellationToken ct);
    Task<Branch> CreateAsync(Guid projectId, string name, CancellationToken ct);
}
