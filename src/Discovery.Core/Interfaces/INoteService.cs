using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface INoteService
{
    Task<CursorPageDto<EntityNote>> GetPageAsync(Guid? clientId, Guid? siteId, Guid? agentId, string? cursor, int limit, CancellationToken ct = default);
    Task<EntityNote?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<EntityNote> CreateAsync(EntityNote note, CancellationToken ct = default);
    Task UpdateAsync(EntityNote note, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
