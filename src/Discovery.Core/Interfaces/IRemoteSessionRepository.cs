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

    /// <summary>Sessões aguardando consentimento cujo prazo já venceu (sweeper).</summary>
    Task<IEnumerable<RemoteSession>> GetPendingConsentPastDeadlineAsync(DateTime before, CancellationToken ct = default);

    /// <summary>
    /// Sessões ACTIVE criadas antes do cutoff e que nunca tiveram atividade do
    /// viewer (credenciais/renovação). O sweeper fecha essas sessões órfãs.
    /// </summary>
    Task<IEnumerable<RemoteSession>> GetNeverActivatedActiveSessionsAsync(DateTime startedBefore, CancellationToken ct = default);

    /// <summary>Marca atividade do viewer (credenciais emitidas) sem sobrescrever o resto da linha.</summary>
    Task TouchActivityAsync(Guid id, DateTime at, CancellationToken ct = default);
}
