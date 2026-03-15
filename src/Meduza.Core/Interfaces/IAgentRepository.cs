using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IAgentRepository
{
    Task<Agent?> GetByIdAsync(Guid id);
    Task<IEnumerable<Agent>> GetAllAsync();
    Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId);
    Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId);
    Task<Agent> CreateAsync(Agent agent);
    Task UpdateAsync(Agent agent);
    Task UpdateStatusAsync(Guid id, Enums.AgentStatus status, string? ipAddress);
    Task DeleteAsync(Guid id);
}
