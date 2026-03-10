using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IAiChatSessionRepository
{
    Task<AiChatSession> CreateAsync(AiChatSession session, CancellationToken ct = default);
    Task<AiChatSession?> GetByIdAsync(Guid id, Guid agentId, CancellationToken ct = default);
    Task<List<AiChatSession>> GetByAgentAsync(Guid agentId, int limit, CancellationToken ct = default);
    Task<List<AiChatSession>> GetExpiredAsync(DateTime cutoff, int limit, CancellationToken ct = default);
    Task<int> SoftDeleteAsync(Guid id, CancellationToken ct = default);
    Task<int> HardDeleteAsync(DateTime deletedBefore, CancellationToken ct = default);
}
