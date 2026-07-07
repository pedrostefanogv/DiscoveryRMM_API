using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Serviço de domínio que orquestra transições de estado de workflow
/// de tickets, extraindo a lógica antes embutida no controller.
/// </summary>
public class TicketWorkflowService : ITicketWorkflowService
{
    private readonly ITicketRepository _ticketRepo;
    private readonly IWorkflowRepository _workflowRepo;
    private readonly ISlaService _slaService;
    private readonly IActivityLogService _activityLogService;
    private readonly ITicketAlertRuleRepository _alertRuleRepo;
    private readonly IAlertDispatchService _alertDispatchService;
    private readonly INotificationService _notificationService;
    private readonly ILogger<TicketWorkflowService> _logger;

    public TicketWorkflowService(
        ITicketRepository ticketRepo,
        IWorkflowRepository workflowRepo,
        ISlaService slaService,
        IActivityLogService activityLogService,
        ITicketAlertRuleRepository alertRuleRepo,
        IAlertDispatchService alertDispatchService,
        INotificationService notificationService,
        ILogger<TicketWorkflowService> logger)
    {
        _ticketRepo = ticketRepo;
        _workflowRepo = workflowRepo;
        _slaService = slaService;
        _activityLogService = activityLogService;
        _alertRuleRepo = alertRuleRepo;
        _alertDispatchService = alertDispatchService;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task<Ticket> TransitionAsync(
        Guid ticketId,
        Guid targetStateId,
        Guid? changedByUserId,
        CancellationToken ct = default)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            throw new InvalidOperationException($"Ticket {ticketId} not found");

        // Carregar estados em paralelo
        var oldStateTask = _workflowRepo.GetStateByIdAsync(ticket.WorkflowStateId);
        var newStateTask = _workflowRepo.GetStateByIdAsync(targetStateId);
        await Task.WhenAll(oldStateTask, newStateTask);

        var oldState = oldStateTask.Result;
        var newState = newStateTask.Result;

        // Validar transição
        var valid = await _workflowRepo.IsTransitionValidAsync(ticket.WorkflowStateId, targetStateId, ticket.ClientId);
        if (!valid)
            throw new InvalidOperationException("Invalid workflow transition");

        // ClosedAt
        DateTime? closedAt = newState?.IsFinal == true ? DateTime.UtcNow : null;

        // --- SLA Hold: pausar/retomar ---
        var wasOnHold = oldState?.PausesSla == true;
        var willBeOnHold = newState?.PausesSla == true;

        if (!wasOnHold && willBeOnHold)
        {
            await _ticketRepo.UpdateSlaHoldAsync(ticketId, DateTime.UtcNow, ticket.SlaPausedSeconds);
        }
        else if (wasOnHold && !willBeOnHold && ticket.SlaHoldStartedAt.HasValue)
        {
            var addedSeconds = (int)(DateTime.UtcNow - ticket.SlaHoldStartedAt.Value).TotalSeconds;
            await _ticketRepo.UpdateSlaHoldAsync(ticketId, null, ticket.SlaPausedSeconds + addedSeconds);
        }

        // Persistir transição com ExecuteUpdate
        await _ticketRepo.UpdateWorkflowStateAsync(ticketId, targetStateId, closedAt);

        // Log da mudança
        await _activityLogService.LogStateChangeAsync(ticketId, changedByUserId, ticket.WorkflowStateId, targetStateId);

        // --- Alertas PSADT em paralelo ---
        var alertRules = await _alertRuleRepo.GetByWorkflowStateIdAsync(targetStateId);
        if (alertRules.Any())
        {
            var alertTasks = alertRules.Select(rule => DispatchAlertSafeAsync(rule, ticket, ct));
            await Task.WhenAll(alertTasks);
        }

        // Recarregar do banco
        var updatedTicket = await _ticketRepo.GetByIdAsync(ticketId);

        // Notificar assignee
        if (updatedTicket?.AssignedToUserId.HasValue == true)
        {
            var stateLabel = newState?.Name ?? targetStateId.ToString();
            await _notificationService.PublishAsync(new NotificationPublishRequest(
                EventType: "ticket.state_changed",
                Topic: "tickets",
                Title: "Estado do ticket alterado",
                Message: $"O ticket #{ticketId} '{updatedTicket.Title}' mudou para o estado '{stateLabel}'.",
                Severity: NotificationSeverity.Informational,
                Payload: new { ticketId, workflowStateId = targetStateId },
                RecipientUserId: updatedTicket.AssignedToUserId
            ), ct);
        }

        return updatedTicket!;
    }

    private async Task DispatchAlertSafeAsync(
        TicketAlertRule rule,
        Ticket ticket,
        CancellationToken ct)
    {
        try
        {
            var (scopeType, agentId, siteId, clientId) = ResolveAlertScope(ticket, rule.ScopePreference);
            var alertDef = new AgentAlertDefinition
            {
                Id = Guid.NewGuid(),
                Title = rule.Title,
                Message = rule.Message,
                AlertType = rule.AlertType,
                TimeoutSeconds = rule.TimeoutSeconds,
                ActionsJson = rule.ActionsJson,
                DefaultAction = rule.DefaultAction,
                Icon = rule.Icon,
                ScopeType = scopeType,
                ScopeAgentId = agentId,
                ScopeSiteId = siteId,
                ScopeClientId = clientId,
                TicketId = ticket.Id,
                Status = AlertDefinitionStatus.Draft,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _alertDispatchService.DispatchAsync(alertDef);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alert dispatch failed for rule {RuleId} on ticket {TicketId}", rule.Id, ticket.Id);
        }
    }

    private static (AlertScopeType scopeType, Guid? agentId, Guid? siteId, Guid? clientId) ResolveAlertScope(
        Ticket ticket,
        AlertScopeType scopePreference)
    {
        switch (scopePreference)
        {
            case AlertScopeType.Agent:
                return (AlertScopeType.Agent, ticket.AgentId, null, null);
            case AlertScopeType.Site:
                return (AlertScopeType.Site, null, ticket.SiteId, null);
            case AlertScopeType.Label:
                return (AlertScopeType.Label, null, null, null);
            default: // Client
                return (AlertScopeType.Client, null, null, ticket.ClientId);
        }
    }
}
