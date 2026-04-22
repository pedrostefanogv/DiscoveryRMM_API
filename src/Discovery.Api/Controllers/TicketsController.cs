using Discovery.Api.Services;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TicketsController : ControllerBase
{
    private readonly ITicketRepository _repo;
    private readonly IWorkflowRepository _workflowRepo;
    private readonly IDepartmentRepository _departmentRepo;
    private readonly IWorkflowProfileRepository _workflowProfileRepo;
    private readonly ISlaService _slaService;
    private readonly IActivityLogService _activityLogService;
    private readonly IAttachmentService _attachmentService;
    private readonly IServerConfigurationRepository _serverConfigurationRepository;
    private readonly ITicketAlertRuleRepository _ticketAlertRuleRepo;
    private readonly AlertDispatchService _alertDispatchService;
    private readonly INotificationService _notificationService;
    private readonly ITicketWatcherRepository _watcherRepo;

    public TicketsController(
        ITicketRepository repo,
        IWorkflowRepository workflowRepo,
        IDepartmentRepository departmentRepo,
        IWorkflowProfileRepository workflowProfileRepo,
        ISlaService slaService,
        IActivityLogService activityLogService,
        IAttachmentService attachmentService,
        IServerConfigurationRepository serverConfigurationRepository,
        ITicketAlertRuleRepository ticketAlertRuleRepo,
        AlertDispatchService alertDispatchService,
        INotificationService notificationService,
        ITicketWatcherRepository watcherRepo)
    {
        _repo = repo;
        _workflowRepo = workflowRepo;
        _departmentRepo = departmentRepo;
        _workflowProfileRepo = workflowProfileRepo;
        _slaService = slaService;
        _activityLogService = activityLogService;
        _attachmentService = attachmentService;
        _serverConfigurationRepository = serverConfigurationRepository;
        _ticketAlertRuleRepo = ticketAlertRuleRepo;
        _alertDispatchService = alertDispatchService;
        _notificationService = notificationService;
        _watcherRepo = watcherRepo;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] TicketFilterQuery filter)
    {
        var tickets = await _repo.GetAllAsync(filter);
        return Ok(tickets);
    }

    [HttpGet("by-client/{clientId:guid}")]
    public async Task<IActionResult> GetByClient(Guid clientId, [FromQuery] Guid? workflowStateId)
    {
        var tickets = await _repo.GetByClientIdAsync(clientId, workflowStateId);
        return Ok(tickets);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var ticket = await _repo.GetByIdAsync(id);
        return ticket is null ? NotFound() : Ok(ticket);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTicketRequest request)
    {
        // Buscar estado inicial do workflow (global ou do client)
        var initialState = await _workflowRepo.GetInitialStateAsync(request.ClientId);
        if (initialState is null)
            return BadRequest("No initial workflow state configured.");

        // Validar departamento se fornecido
        if (request.DepartmentId.HasValue)
        {
            var department = await _departmentRepo.GetByIdAsync(request.DepartmentId.Value);
            if (department is null)
                return BadRequest("Departamento não encontrado.");
        }

        // Validar e carregar workflow profile para calcular SLA
        WorkflowProfile? workflowProfile = null;
        DateTime? slaExpiresAt = null;

        if (request.WorkflowProfileId.HasValue)
        {
            workflowProfile = await _workflowProfileRepo.GetByIdAsync(request.WorkflowProfileId.Value);
            if (workflowProfile is null)
                return BadRequest("Perfil de workflow não encontrado.");
        }
        else if (request.DepartmentId.HasValue)
        {
            // Se não informou profile, pegar o padrão do departamento
            workflowProfile = await _workflowProfileRepo.GetDefaultByDepartmentAsync(request.DepartmentId.Value);
        }

        // Calcular SLA se houver profile
        if (workflowProfile != null)
        {
            var now = DateTime.UtcNow;
            slaExpiresAt = await _slaService.CalculateSlaExpiryAsync(workflowProfile.Id, now);
        }

        var effectiveWorkflowProfileId = workflowProfile?.Id;

        var ticket = new Ticket
        {
            ClientId = request.ClientId,
            SiteId = request.SiteId,
            AgentId = request.AgentId,
            DepartmentId = request.DepartmentId,
            WorkflowProfileId = effectiveWorkflowProfileId,
            Title = request.Title,
            Description = request.Description,
            Priority = request.Priority ?? (workflowProfile?.DefaultPriority ?? TicketPriority.Medium),
            Category = request.Category,
            AssignedToUserId = request.AssignedToUserId,
            WorkflowStateId = initialState.Id,
            SlaExpiresAt = slaExpiresAt
        };

        var created = await _repo.CreateAsync(ticket);

        // Log da criação
        await _activityLogService.LogActivityAsync(
            created.Id,
            TicketActivityType.Created,
            null,
            null,
            initialState.Id.ToString(),
            "Ticket criado"
        );

        // Notificar usuário atribuído na criação
        if (created.AssignedToUserId.HasValue)
        {
            await _notificationService.PublishAsync(new NotificationPublishRequest(
                EventType: "ticket.assigned",
                Topic: "tickets",
                Title: "Ticket atribuído a você",
                Message: $"O ticket #{created.Id} '{created.Title}' foi atribuído a você.",
                Severity: NotificationSeverity.Informational,
                Payload: new { ticketId = created.Id },
                RecipientUserId: created.AssignedToUserId
            ));
        }

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketRequest request)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        var oldPriority = ticket.Priority;
        var oldAssignedTo = ticket.AssignedToUserId;

        ticket.Title = request.Title;
        ticket.Description = request.Description;
        ticket.Category = request.Category;

        // Atualizar prioridade se changing
        if (request.Priority != oldPriority)
        {
            ticket.Priority = request.Priority;
            await _activityLogService.LogPriorityChangeAsync(
                id, null, oldPriority.ToString(), request.Priority.ToString()
            );
        }

        // Atualizar atribuição se mudou
        if (request.AssignedToUserId != oldAssignedTo)
        {
            ticket.AssignedToUserId = request.AssignedToUserId;
            await _activityLogService.LogAssignmentAsync(id, null, oldAssignedTo, request.AssignedToUserId);

            if (request.AssignedToUserId.HasValue)
            {
                await _notificationService.PublishAsync(new NotificationPublishRequest(
                    EventType: "ticket.assigned",
                    Topic: "tickets",
                    Title: "Ticket atribuído a você",
                    Message: $"O ticket #{id} '{ticket.Title}' foi atribuído a você.",
                    Severity: NotificationSeverity.Informational,
                    Payload: new { ticketId = id },
                    RecipientUserId: request.AssignedToUserId
                ));
            }
        }

        await _repo.UpdateAsync(ticket);
        return Ok(ticket);
    }

    [HttpPatch("{id:guid}/workflow-state")]
    public async Task<IActionResult> UpdateWorkflowState(Guid id, [FromBody] UpdateWorkflowStateRequest request)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        // Validar se a transição é permitida
        var valid = await _workflowRepo.IsTransitionValidAsync(ticket.WorkflowStateId, request.WorkflowStateId, ticket.ClientId);
        if (!valid) return BadRequest("Invalid workflow transition.");

        var oldStateId = ticket.WorkflowStateId;

        // Verificar se o novo estado é final (para setar ClosedAt)
        var newState = await _workflowRepo.GetStateByIdAsync(request.WorkflowStateId);
        DateTime? closedAt = newState?.IsFinal == true ? DateTime.UtcNow : null;
        ticket.ClosedAt = closedAt;

        await _repo.UpdateWorkflowStateAsync(id, request.WorkflowStateId, closedAt);

        // --- SLA Hold: pausar/retomar baseado em PausesSla do novo estado ---
        var oldState = await _workflowRepo.GetStateByIdAsync(oldStateId);
        var wasOnHold = oldState?.PausesSla == true;
        var willBeOnHold = newState?.PausesSla == true;

        if (!wasOnHold && willBeOnHold)
        {
            // Entrou em pausa
            await _repo.UpdateSlaHoldAsync(id, DateTime.UtcNow, ticket.SlaPausedSeconds);
        }
        else if (wasOnHold && !willBeOnHold && ticket.SlaHoldStartedAt.HasValue)
        {
            // Saiu da pausa: acumular tempo pausado
            var addedSeconds = (int)(DateTime.UtcNow - ticket.SlaHoldStartedAt.Value).TotalSeconds;
            await _repo.UpdateSlaHoldAsync(id, null, ticket.SlaPausedSeconds + addedSeconds);
        }

        // Log da mudança de estado
        await _activityLogService.LogStateChangeAsync(id, null, oldStateId, request.WorkflowStateId);

        // Disparar alertas automáticos vinculados ao novo estado
        var alertRules = await _ticketAlertRuleRepo.GetByWorkflowStateIdAsync(request.WorkflowStateId);
        foreach (var rule in alertRules)
        {
            var (scopeType, agentId, siteId, clientId) = ResolveAlertScope(ticket, rule.ScopePreference);
            var alertDef = new Discovery.Core.Entities.AgentAlertDefinition
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
                TicketId = id,
                Status = Discovery.Core.Enums.AlertDefinitionStatus.Draft,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            try { await _alertDispatchService.DispatchAsync(alertDef); }
            catch (Exception ex)
            {
                // Alerta falhou — não bloquear a transição de estado
                _ = ex;
            }
        }

        // Recarregar o ticket atualizado do banco
        var updatedTicket = await _repo.GetByIdAsync(id);

        // Notificar atribuído sobre mudança de estado
        if (updatedTicket?.AssignedToUserId.HasValue == true)
        {
            var stateLabel = newState?.Name ?? request.WorkflowStateId.ToString();
            await _notificationService.PublishAsync(new NotificationPublishRequest(
                EventType: "ticket.state_changed",
                Topic: "tickets",
                Title: "Estado do ticket alterado",
                Message: $"O ticket #{id} '{updatedTicket.Title}' mudou para o estado '{stateLabel}'.",
                Severity: NotificationSeverity.Informational,
                Payload: new { ticketId = id, workflowStateId = request.WorkflowStateId },
                RecipientUserId: updatedTicket.AssignedToUserId
            ));
        }

        return Ok(new { message = "Workflow state updated", ticket = updatedTicket });
    }

    // --- Comments ---

    [HttpGet("{id:guid}/comments")]
    public async Task<IActionResult> GetComments(Guid id)
    {
        var comments = await _repo.GetCommentsAsync(id);
        return Ok(comments);
    }

    [HttpPost("{id:guid}/comments")]
    public async Task<IActionResult> AddComment(Guid id, [FromBody] AddCommentRequest request)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        var comment = new TicketComment
        {
            TicketId = id,
            Author = request.Author,
            Content = request.Content,
            IsInternal = request.IsInternal
        };
        var created = await _repo.AddCommentAsync(comment);

        await _activityLogService.LogActivityAsync(
            id,
            TicketActivityType.Commented,
            null,
            null,
            null,
            $"Comentário adicionado por {created.Author}");

        // Registrar primeira resposta (FRT): se o ticket tem assignee e este é o primeiro comentário do assignee
        if (!ticket.FirstRespondedAt.HasValue && ticket.AssignedToUserId.HasValue
            && request.Author == ticket.AssignedToUserId.Value.ToString())
        {
            await _repo.UpdateFirstRespondedAtAsync(id, DateTime.UtcNow);
        }

        // Notificar atribuído e watchers sobre novo comentário público
        if (!request.IsInternal)
        {
            var shortContent = created.Content[..Math.Min(created.Content.Length, 120)];
            var notifyMsg = $"{created.Author} comentou no ticket '{ticket.Title}': {shortContent}";

            if (ticket.AssignedToUserId.HasValue)
            {
                await _notificationService.PublishAsync(new NotificationPublishRequest(
                    EventType: "ticket.comment",
                    Topic: "tickets",
                    Title: "Novo comentário no ticket",
                    Message: notifyMsg,
                    Severity: NotificationSeverity.Informational,
                    Payload: new { ticketId = id, commentId = created.Id },
                    RecipientUserId: ticket.AssignedToUserId
                ));
            }

            var watchers = await _watcherRepo.GetByTicketAsync(id);
            foreach (var watcher in watchers.Where(w => w.UserId != ticket.AssignedToUserId))
            {
                await _notificationService.PublishAsync(new NotificationPublishRequest(
                    EventType: "ticket.comment",
                    Topic: "tickets",
                    Title: "Novo comentário no ticket que você segue",
                    Message: notifyMsg,
                    Severity: NotificationSeverity.Informational,
                    Payload: new { ticketId = id, commentId = created.Id },
                    RecipientUserId: watcher.UserId
                ));
            }
        }

        return Created($"api/tickets/{id}/comments", created);
    }

    [HttpGet("{id:guid}/attachments")]
    public async Task<IActionResult> GetAttachments(Guid id)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null)
            return NotFound();

        var attachments = await _attachmentService.GetAttachmentsForEntityAsync("Ticket", id);
        return Ok(attachments);
    }

    [HttpPost("{id:guid}/attachments/presigned-upload")]
    public async Task<IActionResult> PrepareAttachmentUpload(Guid id, [FromBody] PrepareTicketAttachmentUploadRequest request)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.FileName) || string.IsNullOrWhiteSpace(request.ContentType))
            return BadRequest(new { error = "FileName e ContentType são obrigatórios." });

        if (request.SizeBytes <= 0)
            return BadRequest(new { error = "SizeBytes deve ser maior que zero." });

        var settings = await GetTicketAttachmentSettingsAsync();
        if (!settings.Enabled)
            return BadRequest(new { error = "Upload de anexos para tickets está desabilitado." });

        if (!settings.IsContentTypeAllowed(request.ContentType))
            return BadRequest(new
            {
                error = "Tipo de arquivo não permitido para tickets.",
                allowedContentTypes = settings.AllowedContentTypes
            });

        if (request.SizeBytes > settings.MaxFileSizeBytes)
            return BadRequest(new
            {
                error = "Arquivo excede o tamanho máximo permitido.",
                maxFileSizeBytes = settings.MaxFileSizeBytes
            });

        var prepared = await _attachmentService.PreparePresignedUploadAsync(
            "Ticket",
            id,
            ticket.ClientId,
            request.FileName,
            request.ContentType,
            request.SizeBytes,
            settings.PresignedUploadUrlTtlMinutes);

        return Ok(prepared);
    }

    [HttpPost("{id:guid}/attachments/complete-upload")]
    public async Task<IActionResult> CompleteAttachmentUpload(Guid id, [FromBody] CompleteTicketAttachmentUploadRequest request)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null)
            return NotFound();

        if (request.AttachmentId == Guid.Empty)
            return BadRequest(new { error = "AttachmentId inválido." });

        if (string.IsNullOrWhiteSpace(request.ObjectKey) ||
            string.IsNullOrWhiteSpace(request.FileName) ||
            string.IsNullOrWhiteSpace(request.ContentType))
        {
            return BadRequest(new { error = "ObjectKey, FileName e ContentType são obrigatórios." });
        }

        if (request.SizeBytes <= 0)
            return BadRequest(new { error = "SizeBytes deve ser maior que zero." });

        var settings = await GetTicketAttachmentSettingsAsync();
        if (!settings.Enabled)
            return BadRequest(new { error = "Upload de anexos para tickets está desabilitado." });

        if (!settings.IsContentTypeAllowed(request.ContentType))
            return BadRequest(new
            {
                error = "Tipo de arquivo não permitido para tickets.",
                allowedContentTypes = settings.AllowedContentTypes
            });

        if (request.SizeBytes > settings.MaxFileSizeBytes)
            return BadRequest(new
            {
                error = "Arquivo excede o tamanho máximo permitido.",
                maxFileSizeBytes = settings.MaxFileSizeBytes
            });

        var expectedPrefix = $"clients/{ticket.ClientId:N}/ticket/{id:N}/attachments/{request.AttachmentId:N}/";
        if (!request.ObjectKey.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "ObjectKey inválido para este ticket/cliente." });

        var attachment = await _attachmentService.CompletePresignedUploadAsync(
            request.AttachmentId,
            "Ticket",
            id,
            ticket.ClientId,
            request.FileName,
            request.ContentType,
            request.SizeBytes,
            request.ObjectKey,
            request.UploadedBy);

        return Created($"api/tickets/{id}/attachments/{attachment.Id}", attachment);
    }

    /// <summary>
    /// Deleta (soft delete) um ticket.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        await _repo.DeleteAsync(id);

        // Log da exclusão
        await _activityLogService.LogActivityAsync(
            id,
            TicketActivityType.Deleted,
            null,
            null,
            null,
            "Ticket marcado como deletado"
        );

        return NoContent();
    }

    private async Task<TicketAttachmentSettings> GetTicketAttachmentSettingsAsync()
    {
        var serverConfig = await _serverConfigurationRepository.GetOrCreateDefaultAsync();
        return TicketAttachmentSettings.FromJson(serverConfig.TicketAttachmentSettingsJson);
    }

    /// <summary>
    /// Resolve o escopo de alerta a partir do contexto do ticket,
    /// respeitando a preferência configurada na regra com fallback Agent→Site→Client.
    /// </summary>
    private static (AlertScopeType, Guid?, Guid?, Guid?) ResolveAlertScope(
        Ticket ticket, AlertScopeType preference)
    {
        if (preference == AlertScopeType.Agent && ticket.AgentId.HasValue)
            return (AlertScopeType.Agent, ticket.AgentId, null, null);

        if ((preference == AlertScopeType.Agent || preference == AlertScopeType.Site) && ticket.SiteId.HasValue)
            return (AlertScopeType.Site, null, ticket.SiteId, null);

        return (AlertScopeType.Client, null, null, ticket.ClientId);
    }
}

public record CreateTicketRequest(
    Guid ClientId,
    Guid? SiteId,
    Guid? AgentId,
    Guid? DepartmentId,
    Guid? WorkflowProfileId,
    string Title,
    string Description,
    TicketPriority? Priority,
    string? Category,
    Guid? AssignedToUserId);

public record UpdateTicketRequest(
    string Title,
    string Description,
    TicketPriority Priority,
    Guid? AssignedToUserId,
    string? Category);

public record UpdateWorkflowStateRequest(Guid WorkflowStateId);

public record AddCommentRequest(string Author, string Content, bool IsInternal);

public record PrepareTicketAttachmentUploadRequest(
    string FileName,
    string ContentType,
    long SizeBytes);

public record CompleteTicketAttachmentUploadRequest(
    Guid AttachmentId,
    string ObjectKey,
    string FileName,
    string ContentType,
    long SizeBytes,
    string? UploadedBy);
