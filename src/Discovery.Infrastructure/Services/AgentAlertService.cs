using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class AgentAlertService : IAgentAlertService
{
    private readonly IAgentAlertRepository _repo;
    private readonly ILogger<AgentAlertService> _logger;

    public AgentAlertService(IAgentAlertRepository repo, ILogger<AgentAlertService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<AgentAlertDefinition> CreateAsync(CreateAgentAlertRequest request, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var alert = new AgentAlertDefinition
        {
            Title = request.Title,
            Message = request.Message,
            AlertType = request.AlertType,
            TimeoutSeconds = request.TimeoutSeconds ?? (request.AlertType == PsadtAlertType.Toast ? 15 : null),
            ActionsJson = request.ActionsJson,
            DefaultAction = request.DefaultAction,
            Icon = string.IsNullOrWhiteSpace(request.Icon) ? "info" : request.Icon,
            ScopeType = request.ScopeType,
            ScopeAgentId = request.ScopeAgentId,
            ScopeSiteId = request.ScopeSiteId,
            ScopeClientId = request.ScopeClientId,
            ScopeLabelName = request.ScopeLabelName,
            TicketId = request.TicketId,
            CreatedBy = request.CreatedBy,
            Status = request.ScheduledAt.HasValue
                ? AlertDefinitionStatus.Scheduled
                : AlertDefinitionStatus.Draft,
            ScheduledAt = request.ScheduledAt,
            ExpiresAt = request.ExpiresAt
        };

        var created = await _repo.CreateAsync(alert);
        _logger.LogInformation("AgentAlert {AlertId} criado (tipo={Type}, scope={Scope})", created.Id, created.AlertType, created.ScopeType);
        return created;
    }

    public Task<AgentAlertDefinition?> GetByIdAsync(Guid id)
        => _repo.GetByIdAsync(id);

    public Task<IReadOnlyList<AgentAlertDefinition>> GetAllAsync(
        AlertDefinitionStatus? status = null,
        AlertScopeType? scopeType = null,
        Guid? scopeClientId = null,
        Guid? scopeSiteId = null,
        Guid? scopeAgentId = null,
        Guid? ticketId = null,
        int limit = 100,
        int offset = 0)
        => _repo.GetByFiltersAsync(status, scopeType, scopeClientId, scopeSiteId, scopeAgentId, ticketId, limit, offset);

    public async Task<bool> CancelAsync(Guid id)
    {
        var alert = await _repo.GetByIdAsync(id);
        if (alert is null)
            return false;

        if (alert.Status is AlertDefinitionStatus.Dispatched or AlertDefinitionStatus.Expired or AlertDefinitionStatus.Cancelled)
            return false;

        await _repo.UpdateStatusAsync(id, AlertDefinitionStatus.Cancelled);
        _logger.LogInformation("AgentAlert {AlertId} cancelado.", id);
        return true;
    }
}
