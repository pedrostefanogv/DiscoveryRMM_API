using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface INoteService
{
    Task<IReadOnlyList<EntityNote>> GetByClientIdAsync(Guid clientId, CancellationToken ct = default);
    Task<IReadOnlyList<EntityNote>> GetBySiteIdAsync(Guid siteId, CancellationToken ct = default);
    Task<IReadOnlyList<EntityNote>> GetByAgentIdAsync(Guid agentId, CancellationToken ct = default);
    Task<EntityNote?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<EntityNote> CreateAsync(EntityNote note, CancellationToken ct = default);
    Task UpdateAsync(EntityNote note, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
