using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAgentAlertRepository
{
    Task<AgentAlertDefinition?> GetByIdAsync(Guid id);

    Task<IReadOnlyList<AgentAlertDefinition>> GetByFiltersAsync(
        AlertDefinitionStatus? status = null,
        AlertScopeType? scopeType = null,
        Guid? scopeClientId = null,
        Guid? scopeSiteId = null,
        Guid? scopeAgentId = null,
        Guid? ticketId = null,
        int limit = 100,
        int offset = 0);

    /// <summary>
    /// Retorna alertas com status Scheduled cujo ScheduledAt &lt;= utcNow.
    /// Usado pelo background scheduler para disparar despachos.
    /// </summary>
    Task<IReadOnlyList<AgentAlertDefinition>> GetPendingScheduledAsync(DateTime utcNow);

    /// <summary>
    /// Retorna alertas Draft/Scheduled com ExpiresAt &lt;= utcNow para marcar como Expired.
    /// </summary>
    Task<IReadOnlyList<AgentAlertDefinition>> GetExpiredAsync(DateTime utcNow);

    Task<AgentAlertDefinition> CreateAsync(AgentAlertDefinition alert);

    Task UpdateAsync(AgentAlertDefinition alert);

    Task UpdateStatusAsync(Guid id, AlertDefinitionStatus status, DateTime? dispatchedAt = null, int? dispatchedCount = null);
}
