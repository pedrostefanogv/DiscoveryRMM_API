using Discovery.Core.Entities;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public sealed class WorkflowProfileService : IWorkflowProfileService
{
    private readonly IWorkflowProfileRepository _repo;
    public WorkflowProfileService(IWorkflowProfileRepository repo) => _repo = repo;

    public Task<WorkflowProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) => _repo.GetByIdAsync(id);
    public async Task<IReadOnlyList<WorkflowProfile>> GetByClientAsync(Guid? clientId, bool includeGlobal = true, CancellationToken ct = default)
    { var items = await _repo.GetByClientAsync(clientId, includeGlobal); return items.AsReadOnly(); }
    public async Task<IReadOnlyList<WorkflowProfile>> GetGlobalAsync(CancellationToken ct = default)
    { var items = await _repo.GetGlobalAsync(); return items.AsReadOnly(); }
    public Task<WorkflowProfile> CreateAsync(WorkflowProfile profile, CancellationToken ct = default) => _repo.CreateAsync(profile);
    public Task<WorkflowProfile> UpdateAsync(WorkflowProfile profile, CancellationToken ct = default) => _repo.UpdateAsync(profile);
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => _repo.DeleteAsync(id);
}
