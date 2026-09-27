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

        // Carregar estados em SEQUÊNCIA. Ambos os repositórios compartilham o
        // mesmo DbContext com escopo de request: disparar as duas queries em
        // paralelo disparava "A second operation was started on this context
        // instance before a previous operation completed" de forma
        // intermitente, derrubando a transição (e a transaction do handler)
        // mesmo com o chamado e os estados corretos.
        var oldState = await _workflowRepo.GetStateByIdAsync(ticket.WorkflowStateId);
        var newState = await _workflowRepo.GetStateByIdAsync(targetStateId);

        // Estado de destino inexistente: erro explícito em vez de "transição
        // inválida", que confundia estado removido com transição não cadastrada.
        if (newState is null)
            throw new InvalidOperationException(
                $"O estado de destino {targetStateId} não existe. Atualize a página e escolha um estado válido.");

        // M3: chamado com estado órfão (Guid.Empty ou estado removido) não tinha
        // nenhuma transição válida a partir da origem e ficava impossível de
        // fechar. Nesse caso adota o estado inicial do cliente como origem
        // efetiva, permitindo a transição sem depender da sanitização manual.
        var fromStateId = ticket.WorkflowStateId;
        if (oldState is null)
        {
            var initialState = await _workflowRepo.GetInitialStateAsync(ticket.ClientId);
            if (initialState is not null)
            {
                fromStateId = initialState.Id;
                oldState = initialState;
                _logger.LogWarning(
                    "Ticket {TicketId} tinha estado órfão {OrphanStateId}; adotando o estado inicial {InitialStateId} como origem da transição.",
                    ticketId, ticket.WorkflowStateId, initialState.Id);
            }
        }

        // Validar transição
        var valid = await _workflowRepo.IsTransitionValidAsync(fromStateId, targetStateId, ticket.ClientId);

        // Regra de produto (Configurações → Workflow): qualquer chamado pode ser
        // levado para um estado INICIAL ou FINAL mesmo sem uma transição
        // origem→destino cadastrada. Sem isso, um workflow que só define estados
        // (sem a malha completa de transições) deixava o chamado impossível de
        // fechar — exatamente o sintoma relatado em produção.
        if (!valid && (newState.IsInitial || newState.IsFinal))
        {
            valid = true;
            _logger.LogInformation(
                "Ticket {TicketId}: transição {FromStateId}→{ToStateId} liberada pela regra de estado inicial/final.",
                ticketId, fromStateId, targetStateId);
        }

        if (!valid)
            throw new InvalidOperationException(
                $"Transição inválida: '{oldState?.Name ?? "sem estado"}' → '{newState.Name}'. " +
                "Cadastre a transição em Configurações → Workflow, ou use um estado inicial/final.");

        // ClosedAt
        DateTime? closedAt = newState?.IsFinal == true ? DateTime.UtcNow : null;

        // --- SLA Hold: pausar/retomar ---
        var wasOnHold = oldState?.PausesSla == true;
        var willBeOnHold = newState?.PausesSla == true;

        // Transição + SLA-hold em UM único ExecuteUpdate (elimina a corrida entre
        // transição e close/reabertura — lost update).
        if (!wasOnHold && willBeOnHold)
        {
            await _ticketRepo.UpdateWorkflowStateWithSlaHoldAsync(
                ticketId, targetStateId, closedAt, DateTime.UtcNow, ticket.SlaPausedSeconds);
        }
        else if (wasOnHold && !willBeOnHold && ticket.SlaHoldStartedAt.HasValue)
        {
            var addedSeconds = (int)(DateTime.UtcNow - ticket.SlaHoldStartedAt.Value).TotalSeconds;
            await _ticketRepo.UpdateWorkflowStateWithSlaHoldAsync(
                ticketId, targetStateId, closedAt, null, ticket.SlaPausedSeconds + addedSeconds);
        }
        else
        {
            await _ticketRepo.UpdateWorkflowStateWithSlaHoldAsync(
                ticketId, targetStateId, closedAt, ticket.SlaHoldStartedAt, ticket.SlaPausedSeconds);
        }

        // Log da mudança (usa a origem efetiva, já reparada se era órfã).
        await _activityLogService.LogStateChangeAsync(ticketId, changedByUserId, fromStateId, targetStateId);

        // --- Alertas PSADT (sequencial) ---
        // O dispatch consulta a base (regras/escopo) no mesmo DbContext do
        // request; em paralelo ele pode colidir com o ExecuteUpdate acima.
        var alertRules = await _alertRuleRepo.GetByWorkflowStateIdAsync(targetStateId);
        foreach (var rule in alertRules)
        {
            await DispatchAlertSafeAsync(rule, ticket, ct);
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
                Payload: new { ticketId, ticketTitle = updatedTicket.Title, workflowStateId = targetStateId },
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
