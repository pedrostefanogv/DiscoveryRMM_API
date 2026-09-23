using Discovery.Core.Cqrs.Tickets.Dtos;
using Discovery.Core.Cqrs.Tickets.Events;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação de ITicketCommandService.
/// Encapsula a criação, atualização e orquestração de tickets.
/// </summary>
public sealed class TicketCommandService : ITicketCommandService
{
    private readonly ITicketRepository _repo;
    private readonly IActivityLogService _activityLog;
    private readonly INotificationService _notification;
    private readonly IWorkflowRepository _workflowRepo;
    private readonly ISlaService _slaService;
    private readonly IMediator _mediator;

    public TicketCommandService(
        ITicketRepository repo,
        IActivityLogService activityLog,
        INotificationService notification,
        IWorkflowRepository workflowRepo,
        ISlaService slaService,
        IMediator mediator)
    {
        _repo = repo;
        _activityLog = activityLog;
        _notification = notification;
        _workflowRepo = workflowRepo;
        _slaService = slaService;
        _mediator = mediator;
    }

    public async Task<Ticket> CreateTicketAsync(
        string title, string description, TicketPriority priority,
        Guid clientId, Guid? siteId, Guid? agentId, Guid? departmentId,
        Guid? workflowProfileId, Guid? assignedToUserId, string? category,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Estado inicial do workflow do cliente (sem ele o ticket fica "órfão"
        // de estado e as transições não funcionam).
        var initialState = await _workflowRepo.GetInitialStateAsync(clientId);

        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            Priority = priority,
            ClientId = clientId,
            SiteId = siteId,
            AgentId = agentId,
            DepartmentId = departmentId,
            WorkflowProfileId = workflowProfileId,
            AssignedToUserId = assignedToUserId,
            Category = category,
            WorkflowStateId = initialState?.Id ?? Guid.Empty,
            CreatedAt = now,
            UpdatedAt = now
        };

        // SLA/FRT: calculados a partir do perfil de workflow (quando houver).
        if (workflowProfileId.HasValue)
        {
            try
            {
                ticket.SlaExpiresAt = await _slaService.CalculateSlaExpiryAsync(workflowProfileId.Value, now);
                ticket.SlaFirstResponseExpiresAt = await _slaService.CalculateFirstResponseExpiryAsync(workflowProfileId.Value, now);
            }
            catch (InvalidOperationException)
            {
                // Perfil inexistente/inválido: não bloqueia a criação do chamado.
            }
        }

        await _repo.CreateAsync(ticket);
        await _activityLog.LogActivityAsync(ticket.Id, TicketActivityType.Created,
            null, null, null, "Ticket created");
        await _mediator.Publish(new TicketCreatedEvent(ticket.Id, ticket.Title,
            ticket.ClientId, ticket.SiteId, ticket.AssignedToUserId, ticket.CreatedAt), ct);

        return ticket;
    }

    public async Task<Ticket> UpdateTicketAsync(
        Guid ticketId, string? title, string? description,
        TicketPriority? priority, Guid? departmentId, Guid? workflowProfileId,
        Guid? assignedToUserId, string? category, CancellationToken ct = default)
    {
        var ticket = await _repo.GetByIdAsync(ticketId);
        if (ticket is null)
            throw new KeyNotFoundException($"Ticket {ticketId} not found");

        if (title is not null) ticket.Title = title;
        if (description is not null) ticket.Description = description;

        if (priority.HasValue && priority.Value != ticket.Priority)
        {
            var oldPriority = ticket.Priority;
            ticket.Priority = priority.Value;
            await _activityLog.LogPriorityChangeAsync(ticketId, null,
                oldPriority.ToString(), priority.Value.ToString());
        }

        if (assignedToUserId != ticket.AssignedToUserId)
        {
            var oldAssignee = ticket.AssignedToUserId;
            ticket.AssignedToUserId = assignedToUserId;
            await _activityLog.LogAssignmentAsync(ticketId, null, oldAssignee, assignedToUserId);
            if (assignedToUserId.HasValue)
            {
                await _notification.PublishAsync(new NotificationPublishRequest(
                    "ticket.assigned", "tickets", "Ticket assigned",
                    $"Ticket #{ticketId}", NotificationSeverity.Informational,
                    new { ticketId }, assignedToUserId), ct);
            }
        }

        var oldDepartmentId = ticket.DepartmentId;
        var oldWorkflowProfileId = ticket.WorkflowProfileId;

        if (departmentId.HasValue && departmentId.Value != ticket.DepartmentId)
            ticket.DepartmentId = departmentId.Value;

        if (workflowProfileId.HasValue && workflowProfileId.Value != ticket.WorkflowProfileId)
            ticket.WorkflowProfileId = workflowProfileId.Value;

        ticket.Category = category ?? ticket.Category;
        ticket.UpdatedAt = DateTime.UtcNow;
        await _repo.UpdateAsync(ticket);

        // Logs DEPOIS do update: um log gravado antes da escrita vira órfão se o
        // update falhar.
        if (departmentId.HasValue && departmentId.Value != oldDepartmentId)
            await _activityLog.LogDepartmentChangeAsync(ticketId, null,
                oldDepartmentId?.ToString() ?? "none", departmentId.Value.ToString());

        if (workflowProfileId.HasValue && workflowProfileId.Value != oldWorkflowProfileId)
            await _activityLog.LogActivityAsync(ticketId, TicketActivityType.StateChanged, null,
                oldWorkflowProfileId?.ToString(), workflowProfileId.Value.ToString(), "Workflow profile changed");

        return ticket;
    }

    public async Task<TicketComment> AddCommentAsync(
        Guid ticketId, string content, bool isInternal,
        Guid? userId, string? userName, CancellationToken ct = default)
    {
        var ticket = await _repo.GetByIdAsync(ticketId);
        if (ticket is null)
            throw new KeyNotFoundException($"Ticket {ticketId} not found");

        var comment = new TicketComment
        {
            Id = Guid.NewGuid(),
            TicketId = ticketId,
            Author = userName ?? "system",
            Content = content,
            IsInternal = isInternal,
            CreatedAt = DateTime.UtcNow
        };

        await _repo.AddCommentAsync(comment);

        // Primeira resposta pública marca o FRT (chamados internos não contam).
        if (!isInternal && !ticket.FirstRespondedAt.HasValue)
        {
            ticket.FirstRespondedAt = comment.CreatedAt;
            await _repo.UpdateAsync(ticket);
        }

        await _activityLog.LogActivityAsync(ticketId, TicketActivityType.Commented,
            null, null, null, $"Comment by {comment.Author}");

        return comment;
    }

    public async Task<Ticket> AssignTicketAsync(
        Guid ticketId, Guid? assignedToUserId, Guid? changedByUserId,
        CancellationToken ct = default)
    {
        var ticket = await _repo.GetByIdAsync(ticketId);
        if (ticket is null)
            throw new KeyNotFoundException($"Ticket {ticketId} not found");

        var oldAssignee = ticket.AssignedToUserId;
        ticket.AssignedToUserId = assignedToUserId;
        ticket.UpdatedAt = DateTime.UtcNow;

        await _repo.UpdateAsync(ticket);
        await _activityLog.LogAssignmentAsync(ticketId, changedByUserId,
            oldAssignee, assignedToUserId);

        return ticket;
    }

    /// <summary>Mapeia Ticket → TicketDetailDto.</summary>
    public static TicketDetailDto ToDto(Ticket t) => new(
        t.Id, t.ClientId, t.SiteId, t.AgentId, t.Title, t.Description,
        t.Category, t.Priority, t.WorkflowStateId, t.AssignedToUserId,
        t.SlaExpiresAt, t.SlaBreached, t.CreatedAt, t.UpdatedAt,
        t.ClosedAt, t.DaysOpen);
}
