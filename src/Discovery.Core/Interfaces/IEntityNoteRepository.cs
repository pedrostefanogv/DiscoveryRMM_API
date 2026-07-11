using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IEntityNoteRepository
{
    Task<EntityNote?> GetByIdAsync(Guid id);

    Task<IEnumerable<EntityNote>> GetByClientIdAsync(Guid clientId);
    Task<IEnumerable<EntityNote>> GetBySiteIdAsync(Guid siteId);
    Task<IEnumerable<EntityNote>> GetByAgentIdAsync(Guid agentId);
    Task<CursorPageDto<EntityNote>> GetPageAsync(Guid? clientId, Guid? siteId, Guid? agentId, string? cursor, int limit);

    Task<EntityNote> CreateAsync(EntityNote note);
    Task UpdateAsync(EntityNote note);
    Task DeleteAsync(Guid id);
}