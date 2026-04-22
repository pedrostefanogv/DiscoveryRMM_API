using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAgentUpdateEventRepository
{
    Task<AgentUpdateEvent> CreateAsync(AgentUpdateEvent updateEvent, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentUpdateEvent>> GetByAgentIdAsync(Guid agentId, int limit = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentUpdateRolloutAgentSnapshotDto>> GetRolloutSnapshotsAsync(Guid? clientId = null, Guid? siteId = null, int limit = 200, CancellationToken cancellationToken = default);
}
