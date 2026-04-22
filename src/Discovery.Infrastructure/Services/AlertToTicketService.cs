using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Cria automaticamente um ticket a partir de um AgentAlertDefinition relevante.
/// Usado quando a severidade ou tipo do alerta indica necessidade de chamado.
/// </summary>
public class AlertToTicketService : IAlertToTicketService
{
    private readonly ITicketRepository _ticketRepo;
    private readonly IWorkflowRepository _workflowRepo;
    private readonly IAgentAlertRepository _alertRepo;
    private readonly IActivityLogService _activityLogService;
    private readonly ILogger<AlertToTicketService> _logger;

    public AlertToTicketService(
        ITicketRepository ticketRepo,
        IWorkflowRepository workflowRepo,
        IAgentAlertRepository alertRepo,
        IActivityLogService activityLogService,
        ILogger<AlertToTicketService> logger)
    {
        _ticketRepo = ticketRepo;
        _workflowRepo = workflowRepo;
        _alertRepo = alertRepo;
        _activityLogService = activityLogService;
        _logger = logger;
    }

    /// <summary>
    /// Cria um ticket a partir de um alerta.
    /// Se o alerta já possuir TicketId, retorna o ticket existente sem criar duplicata.
    /// </summary>
    public async Task<Ticket> CreateTicketFromAlertAsync(
        AgentAlertDefinition alert,
        Guid clientId,
        Guid? siteId,
        Guid? agentId,
        TicketPriority priority = TicketPriority.Medium,
        CancellationToken ct = default)
    {
        // Evita duplicata
        if (alert.TicketId.HasValue)
        {
            var existing = await _ticketRepo.GetByIdAsync(alert.TicketId.Value);
            if (existing is not null)
            {
                _logger.LogInformation("Alerta {AlertId} já possui ticket vinculado {TicketId}.", alert.Id, alert.TicketId);
                return existing;
            }
        }

        var initialState = await _workflowRepo.GetInitialStateAsync(clientId);
        if (initialState is null)
        {
            _logger.LogError("Não existe estado inicial de workflow para o cliente {ClientId}.", clientId);
            throw new InvalidOperationException($"Estado inicial de workflow não encontrado para o cliente {clientId}.");
        }

        var ticket = await _ticketRepo.CreateAsync(new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            SiteId = siteId,
            AgentId = agentId,
            Title = $"[Alerta] {alert.Title}",
            Description = $"Ticket criado automaticamente a partir do alerta:\n\n{alert.Message}",
            Category = "Alert",
            WorkflowStateId = initialState.Id,
            Priority = priority,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        await _activityLogService.LogActivityAsync(
            ticket.Id,
            TicketActivityType.AutoCreatedFromAlert,
            null, null,
            "system",
            $"Ticket criado automaticamente a partir do alerta {alert.Id}: {alert.Title}");

        // Vincula o ticket ao alerta
        alert.TicketId = ticket.Id;
        await _alertRepo.UpdateAsync(alert);

        _logger.LogInformation("Ticket {TicketId} criado automaticamente a partir do alerta {AlertId}.", ticket.Id, alert.Id);
        return ticket;
    }

    public async Task<Ticket> CreateTicketFromMonitoringEventAsync(
        AutoTicketCreateTicketRequest request,
        CancellationToken ct = default)
    {
        var initialState = await _workflowRepo.GetInitialStateAsync(request.ClientId);
        if (initialState is null)
        {
            _logger.LogError("Não existe estado inicial de workflow para o cliente {ClientId}.", request.ClientId);
            throw new InvalidOperationException($"Estado inicial de workflow não encontrado para o cliente {request.ClientId}.");
        }

        var ticket = await _ticketRepo.CreateAsync(new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = request.ClientId,
            SiteId = request.SiteId,
            AgentId = request.AgentId,
            DepartmentId = request.DepartmentId,
            WorkflowProfileId = request.WorkflowProfileId,
            Title = request.Title,
            Description = request.Description,
            Category = request.Category ?? "Alert",
            WorkflowStateId = initialState.Id,
            Priority = request.Priority,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        await _activityLogService.LogActivityAsync(
            ticket.Id,
            TicketActivityType.AutoCreatedFromAlert,
            null,
            null,
            "system",
            string.IsNullOrWhiteSpace(request.ActivityMessage)
                ? $"Ticket criado automaticamente a partir de evento de monitoramento: {request.Title}"
                : request.ActivityMessage);

        _logger.LogInformation("Ticket {TicketId} criado automaticamente a partir de evento de monitoramento para o agent {AgentId}.", ticket.Id, request.AgentId);
        return ticket;
    }
}
