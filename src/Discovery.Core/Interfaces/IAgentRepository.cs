using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAgentRepository
{
    Task<Agent?> GetByIdAsync(Guid id);
    Task<IEnumerable<Agent>> GetAllAsync();
    Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId);
    Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId);
    Task<Agent> CreateAsync(Agent agent);
    Task UpdateAsync(Agent agent);
    Task UpdateStatusAsync(Guid id, Enums.AgentStatus status, string? ipAddress);
    Task ApproveZeroTouchAsync(Guid agentId);
    Task DeleteAsync(Guid id);
}
