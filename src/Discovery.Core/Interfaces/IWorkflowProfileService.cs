using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IWorkflowProfileService
{
    Task<WorkflowProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowProfile>> GetByClientAsync(Guid? clientId, bool includeGlobal = true, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowProfile>> GetGlobalAsync(CancellationToken ct = default);
    Task<WorkflowProfile> CreateAsync(WorkflowProfile profile, CancellationToken ct = default);
    Task<WorkflowProfile> UpdateAsync(WorkflowProfile profile, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
