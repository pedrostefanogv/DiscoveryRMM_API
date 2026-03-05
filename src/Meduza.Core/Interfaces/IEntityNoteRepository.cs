using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IEntityNoteRepository
{
    Task<EntityNote?> GetByIdAsync(Guid id);

    Task<IEnumerable<EntityNote>> GetByClientIdAsync(Guid clientId);
    Task<IEnumerable<EntityNote>> GetBySiteIdAsync(Guid siteId);
    Task<IEnumerable<EntityNote>> GetByAgentIdAsync(Guid agentId);

    Task<EntityNote> CreateAsync(EntityNote note);
    Task UpdateAsync(EntityNote note);
    Task DeleteAsync(Guid id);
}