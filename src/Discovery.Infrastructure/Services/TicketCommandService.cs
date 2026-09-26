using Discovery.Core.Cqrs.Tickets.Dtos;
using Discovery.Core.Cqrs.Tickets.Events;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Identity;
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
    private readonly IWorkflowProfileRepository _workflowProfileRepo;
    private readonly ISlaService _slaService;
    private readonly ITicketAssignmentService _assignmentService;
    private readonly IMediator _mediator;
    private readonly IUserRepository _userRepository;

    public TicketCommandService(
        ITicketRepository repo,
        IActivityLogService activityLog,
        INotificationService notification,
        IWorkflowRepository workflowRepo,
        IWorkflowProfileRepository workflowProfileRepo,
        ISlaService slaService,
        ITicketAssignmentService assignmentService,
        IMediator mediator,
        IUserRepository userRepository)
    {
        _repo = repo;
        _activityLog = activityLog;
        _notification = notification;
        _workflowRepo = workflowRepo;
        _workflowProfileRepo = workflowProfileRepo;
        _slaService = slaService;
        _assignmentService = assignmentService;
        _mediator = mediator;
        _userRepository = userRepository;
    }

    public async Task<Ticket> CreateTicketAsync(
        string title, string description, TicketPriority priority,
        Guid clientId, Guid? siteId, Guid? agentId, Guid? departmentId,
        Guid? workflowProfileId, Guid? assignedToUserId, string? category,
        CancellationToken ct = default,
        string? submissionSnapshotMarkdown = null,
        Guid? templateId = null,
        string? templateName = null,
        Guid? requesterUserId = null)
    {
        var now = DateTime.UtcNow;

        // Estado inicial do workflow do cliente (sem ele o ticket fica "órfão"
        // de estado e as transições não funcionam).
        var initialState = await _workflowRepo.GetInitialStateAsync(clientId);

        // B7: sem estado inicial o ticket nasceria com WorkflowStateId = Guid.Empty e
        // todas as transições falhariam. Falha explícita (400) em vez de dado órfão.
        if (initialState is null)
            throw new InvalidOperationException(
                $"Nenhum estado inicial de workflow configurado para o cliente {clientId}. Configure em Workflow.");

        // O console oferece a opção "Padrao do departamento" (envia
        // workflowProfileId nulo). Sem resolver o perfil aqui, o chamado ficava
        // sem SLA mesmo existindo perfil ativo no departamento. Preferência:
        // perfil do próprio cliente > perfil global > primeiro ativo.
        var resolvedProfileId = workflowProfileId;
        if (!resolvedProfileId.HasValue && departmentId.HasValue)
        {
            var candidates = await _workflowProfileRepo.GetByDepartmentAsync(departmentId.Value);
            var preferred = candidates.FirstOrDefault(p => p.ClientId == clientId)
                ?? candidates.FirstOrDefault(p => p.ClientId == null)
                ?? candidates.FirstOrDefault();
            resolvedProfileId = preferred?.Id;
        }

        // Auto-atribuição por estratégia do departamento (round-robin/least-open)
        // quando nenhum responsável foi informado.
        if (!assignedToUserId.HasValue && departmentId.HasValue)
        {
            try
            {
                assignedToUserId = await _assignmentService.ResolveAssigneeAsync(departmentId.Value, ct);
            }
            catch
            {
                // Falha de auto-atribuição não pode impedir a criação do chamado.
                assignedToUserId = null;
            }
        }

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
            WorkflowProfileId = resolvedProfileId,
            AssignedToUserId = assignedToUserId,
            RequesterUserId = requesterUserId,
            Category = category,
            WorkflowStateId = initialState.Id,
            TemplateId = templateId,
            TemplateName = templateName,
            SubmissionSnapshotMarkdown = submissionSnapshotMarkdown,
            CreatedAt = now,
            UpdatedAt = now
        };

        // SLA/FRT: calculados a partir do perfil de workflow (quando houver).
        if (resolvedProfileId.HasValue)
        {
            try
            {
                ticket.SlaExpiresAt = await _slaService.CalculateSlaExpiryAsync(resolvedProfileId.Value, now);
                ticket.SlaFirstResponseExpiresAt = await _slaService.CalculateFirstResponseExpiryAsync(resolvedProfileId.Value, now);
                ticket.FirstResponseSlaStartedAt = now;
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
        Guid? assignedToUserId, string? category,
        bool clearDepartment = false, bool clearWorkflowProfile = false,
        CancellationToken ct = default,
        Guid? requesterUserId = null, bool clearRequester = false,
        Guid? agentId = null, bool clearAgent = false)
    {
        var ticket = await _repo.GetByIdAsync(ticketId);
        if (ticket is null)
            throw new KeyNotFoundException($"Ticket {ticketId} not found");

        var oldDescription = ticket.Description;
        var oldCategory = ticket.Category;

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

        // Solicitante (quem abriu): permite definir/trocar/limpar. Um solicitante
        // inexistente travaria a avaliação (só o solicitante pode avaliar), então
        // o usuário é validado.
        if (requesterUserId.HasValue && !clearRequester)
        {
            var requester = await _userRepository.GetByIdAsync(requesterUserId.Value);
            if (requester is null)
                throw new KeyNotFoundException($"Requester {requesterUserId.Value} not found");
        }

        var newRequesterId = clearRequester
            ? null
            : (requesterUserId.HasValue ? requesterUserId.Value : ticket.RequesterUserId);
        if (newRequesterId != ticket.RequesterUserId)
        {
            // Auditoria própria do solicitante (antes registrava uma atribuição
            // vazia, poluindo a timeline).
            var oldRequester = ticket.RequesterUserId;
            ticket.RequesterUserId = newRequesterId;
            await _activityLog.LogActivityAsync(
                ticketId,
                TicketActivityType.RequesterChanged,
                null,
                oldRequester?.ToString(),
                newRequesterId?.ToString(),
                "Solicitante atualizado");
        }

        // Agent (máquina) vinculado ao chamado.
        var newAgentId = clearAgent
            ? null
            : (agentId.HasValue ? agentId.Value : ticket.AgentId);
        if (newAgentId != ticket.AgentId)
        {
            var oldAgentId = ticket.AgentId;
            ticket.AgentId = newAgentId;
            await _activityLog.LogActivityAsync(
                ticketId,
                TicketActivityType.AgentChanged,
                null,
                oldAgentId?.ToString(),
                newAgentId?.ToString(),
                "Agent do chamado atualizado");
        }

        var oldDepartmentId = ticket.DepartmentId;
        var oldWorkflowProfileId = ticket.WorkflowProfileId;

        // B9: permite limpar departamento/perfil explicitamente (antes, null era
        // indistinguível de "não enviado" e nunca removia o vínculo).
        var newDepartmentId = clearDepartment
            ? null
            : (departmentId.HasValue ? departmentId.Value : ticket.DepartmentId);
        ticket.DepartmentId = newDepartmentId;

        var newWorkflowProfileId = clearWorkflowProfile
            ? null
            : (workflowProfileId.HasValue ? workflowProfileId.Value : ticket.WorkflowProfileId);
        ticket.WorkflowProfileId = newWorkflowProfileId;
        if (clearWorkflowProfile)
        {
            // SLA era derivado do perfil; sem perfil não há prazo.
            ticket.SlaExpiresAt = null;
            ticket.SlaFirstResponseExpiresAt = null;
        }

        ticket.Category = category ?? ticket.Category;
        ticket.UpdatedAt = DateTime.UtcNow;
        await _repo.UpdateAsync(ticket);

        // Logs DEPOIS do update: um log gravado antes da escrita vira órfão se o
        // update falhar.
        if (newDepartmentId != oldDepartmentId)
            await _activityLog.LogDepartmentChangeAsync(ticketId, null,
                oldDepartmentId?.ToString() ?? "none", newDepartmentId?.ToString() ?? "none");

        if (newWorkflowProfileId != oldWorkflowProfileId)
            await _activityLog.LogActivityAsync(ticketId, TicketActivityType.StateChanged, null,
                oldWorkflowProfileId?.ToString(), newWorkflowProfileId?.ToString(), "Workflow profile changed");

        // B8: categoria e descrição passam a ser auditadas.
        if (ticket.Category != oldCategory)
            await _activityLog.LogActivityAsync(ticketId, TicketActivityType.CategoryChanged, null,
                Truncate(oldCategory), Truncate(ticket.Category), "Categoria alterada");

        if (ticket.Description != oldDescription)
            await _activityLog.LogActivityAsync(ticketId, TicketActivityType.DescriptionUpdated, null,
                Truncate(oldDescription), Truncate(ticket.Description), "Descrição atualizada");

        return ticket;
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= 1000 ? value : value[..1000];
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
        t.ClosedAt, t.DaysOpen, t.Rating, t.RatingFeedback, t.RatedAt, t.RatedBy,
        t.SubmissionSnapshotMarkdown, t.TemplateId, t.TemplateName, t.RequesterUserId);
}
