using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAgentAlertService
{
    Task<AgentAlertDefinition> CreateAsync(CreateAgentAlertRequest request, CancellationToken cancellationToken = default);
    Task<AgentAlertDefinition?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<AgentAlertDefinition>> GetAllAsync(
        AlertDefinitionStatus? status = null,
        AlertScopeType? scopeType = null,
        Guid? scopeClientId = null,
        Guid? scopeSiteId = null,
        Guid? scopeAgentId = null,
        Guid? ticketId = null,
        int limit = 100,
        int offset = 0);
    Task<bool> CancelAsync(Guid id);
}

public record CreateAgentAlertRequest(
    string Title,
    string Message,
    PsadtAlertType AlertType,
    int? TimeoutSeconds,
    string? ActionsJson,
    string? DefaultAction,
    string Icon,
    AlertScopeType ScopeType,
    Guid? ScopeAgentId,
    Guid? ScopeSiteId,
    Guid? ScopeClientId,
    string? ScopeLabelName,
    DateTime? ScheduledAt,
    DateTime? ExpiresAt,
    Guid? TicketId,
    string? CreatedBy);
