using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Repositório de sessões remotas.
/// </summary>
public interface IRemoteSessionRepository
{
    Task<RemoteSession?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IEnumerable<RemoteSession>> GetActiveByAgentAsync(Guid agentId, CancellationToken ct = default);
    Task<IEnumerable<RemoteSession>> GetActiveByUserAsync(Guid userId, CancellationToken ct = default);
    Task<int> CountActiveByAgentAsync(Guid agentId, CancellationToken ct = default);
    Task<int> CountActiveByUserAsync(Guid userId, CancellationToken ct = default);
    Task<RemoteSession> CreateAsync(RemoteSession session, CancellationToken ct = default);
    Task<RemoteSession> UpdateAsync(RemoteSession session, CancellationToken ct = default);
    Task<IEnumerable<RemoteSession>> GetExpiredAsync(DateTime before, CancellationToken ct = default);
}
