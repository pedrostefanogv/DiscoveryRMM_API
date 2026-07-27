using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Repositório dedicado para eventos de auditoria de sessões remotas.
/// </summary>
public interface IRemoteSessionAuditRepository
{
    Task AddAsync(RemoteSessionAudit audit, CancellationToken ct = default);
    Task<IEnumerable<RemoteSessionAudit>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default);
}
