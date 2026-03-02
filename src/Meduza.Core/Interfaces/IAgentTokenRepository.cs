using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IAgentTokenRepository
{
    Task<AgentToken?> GetByIdAsync(Guid id);
    Task<AgentToken?> GetByTokenHashAsync(string tokenHash);
    Task<IEnumerable<AgentToken>> GetByAgentIdAsync(Guid agentId);
    Task<AgentToken> CreateAsync(AgentToken token);
    Task UpdateLastUsedAsync(Guid id);
    Task RevokeAsync(Guid id);
    Task RevokeAllByAgentIdAsync(Guid agentId);
}
