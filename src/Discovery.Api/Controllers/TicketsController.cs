using Discovery.Api.Services;
using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.ValueObjects;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
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
    private readonly INotificationService _notificationService;
    private readonly ITicketWatcherRepository _watcherRepo;
    private readonly IScopeContext _scopeContext;
    private readonly IDepartmentCustomFieldService _departmentCustomFieldService;
    private readonly ITicketWorkflowService _ticketWorkflowService;

    public TicketsController(
        ITicketRepository repo,
        IWorkflowRepository workflowRepo,
        IDepartmentRepository departmentRepo,
        IWorkflowProfileRepository workflowProfileRepo,
        ISlaService slaService,
        IActivityLogService activityLogService,
        IAttachmentService attachmentService,
        IServerConfigurationRepository serverConfigurationRepository,
        INotificationService notificationService,
        ITicketWatcherRepository watcherRepo,
        IScopeContext scopeContext,
        IDepartmentCustomFieldService departmentCustomFieldService,
        ITicketWorkflowService ticketWorkflowService)
    {
        _repo = repo;
        _workflowRepo = workflowRepo;
        _departmentRepo = departmentRepo;
        _workflowProfileRepo = workflowProfileRepo;
        _slaService = slaService;
        _activityLogService = activityLogService;
        _attachmentService = attachmentService;
        _serverConfigurationRepository = serverConfigurationRepository;
        _notificationService = notificationService;
        _watcherRepo = watcherRepo;
        _scopeContext = scopeContext;
        _departmentCustomFieldService = departmentCustomFieldService;
        _ticketWorkflowService = ticketWorkflowService;
    }

    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAll([FromQuery] TicketFilterQuery filter)
    {
        var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        var tickets = await _repo.GetAllAsync(filter);
        if (!scope.HasGlobalAccess)
        {
            var allowedClientIds = scope.AllowedClientIds.ToHashSet();
            var allowedSiteIds = scope.AllowedSiteIds.ToHashSet();
            tickets = tickets.Where(t =>
                allowedClientIds.Contains(t.ClientId) ||
                (t.SiteId.HasValue && allowedSiteIds.Contains(t.SiteId.Value)));
        }
        return Ok(tickets);
    }

    [HttpGet("page")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetPage([FromQuery] TicketFilterQuery filter)
    {
        var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        var items = await _repo.GetAllPageAsync(filter);
        var responseItems = new List<Ticket>();
        foreach (var ticket in items)
        {
            if (scope.HasGlobalAccess ||
                scope.AllowedClientIds.Contains(ticket.ClientId) ||
                (ticket.SiteId.HasValue && scope.AllowedSiteIds.Contains(ticket.SiteId.Value)))
            {
                responseItems.Add(ticket);
            }
        }

        var slice = CursorPaginationHelper.SlicePage(responseItems, Math.Clamp(filter.Limit, 1, 500));
        var nextCursor = slice.HasMore && slice.LastItem is not null
            ? CursorPaginationHelper.EncodeCreatedAtCursor(slice.LastItem.CreatedAt, slice.LastItem.Id)
            : null;

        return Ok(new CursorPageDto<Ticket>(
            slice.Page,
            slice.Page.Count,
            filter.Cursor,
            nextCursor,
            slice.HasMore,
            Math.Clamp(filter.Limit, 1, 500)));
    }

    [HttpGet("by-client/{clientId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.FromRoute)]
    public async Task<IActionResult> GetByClient(Guid clientId, [FromQuery] Guid? workflowStateId)
    {
        var tickets = await _repo.GetByClientIdAsync(clientId, workflowStateId);
        return Ok(tickets);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        // Validate scope: user must have access to this ticket's client
        var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (!scope.HasGlobalAccess
            && !scope.AllowedClientIds.Contains(ticket.ClientId)
            && !(ticket.SiteId.HasValue && scope.AllowedSiteIds.Contains(ticket.SiteId.Value)))
        {
            return NotFound();
        }

        return Ok(ticket);
    }

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Create)]
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

        // ── Validar campos customizados do departamento ──
        if (request.DepartmentId.HasValue && request.CustomFieldValues is { Count: > 0 })
        {
            var validationErrors = await _departmentCustomFieldService.ValidateTicketFieldsAsync(
                request.DepartmentId.Value,
                request.CustomFieldValues);

            if (validationErrors.Count > 0)
            {
                return BadRequest(new
                {
                    error = "Validação de campos customizados falhou.",
                    fieldErrors = validationErrors.Select(e => new
                    {
                        e.DefinitionId,
                        e.FieldName,
                        e.ErrorMessage
                    })
                });
            }
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

        // ── Persistir campos customizados do departamento ──
        if (request.DepartmentId.HasValue && request.CustomFieldValues is { Count: > 0 })
        {
            await _departmentCustomFieldService.SaveTicketFieldValuesAsync(
                created.Id,
                request.DepartmentId.Value,
                request.CustomFieldValues,
                HttpContext.Items["Username"] as string ?? "api");
        }

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
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketRequest request)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        // Validate scope: user must have access to the ticket's client
        var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!scope.HasGlobalAccess
            && !scope.AllowedClientIds.Contains(ticket.ClientId)
            && !(ticket.SiteId.HasValue && scope.AllowedSiteIds.Contains(ticket.SiteId.Value)))
        {
            return NotFound(); // Don't leak existence
        }

        var oldPriority = ticket.Priority;
        var oldAssignedTo = ticket.AssignedToUserId;
        var oldDepartmentId = ticket.DepartmentId;

        ticket.Title = request.Title;
        ticket.Description = request.Description;
        ticket.Category = request.Category;

        // Atualizar prioridade se mudou
        if (request.Priority != oldPriority)
        {
            ticket.Priority = request.Priority;
            await _activityLogService.LogPriorityChangeAsync(
                id, null, oldPriority.ToString(), request.Priority.ToString()
            );
        }

        // Atualizar departamento se mudou
        if (request.DepartmentId != oldDepartmentId)
        {
            var oldDeptName = oldDepartmentId.HasValue
                ? (await _departmentRepo.GetByIdAsync(oldDepartmentId.Value))?.Name ?? "unknown"
                : "none";
            var newDeptName = request.DepartmentId.HasValue
                ? (await _departmentRepo.GetByIdAsync(request.DepartmentId.Value))?.Name ?? "unknown"
                : "none";

            ticket.DepartmentId = request.DepartmentId;
            await _activityLogService.LogDepartmentChangeAsync(id, null, oldDeptName, newDeptName);

            // Recalcular workflow profile e SLA se mudou de departamento
            if (request.DepartmentId.HasValue)
            {
                var defaultProfile = await _workflowProfileRepo.GetDefaultByDepartmentAsync(request.DepartmentId.Value);
                if (defaultProfile is not null)
                {
                    ticket.WorkflowProfileId = defaultProfile.Id;
                    ticket.SlaExpiresAt = await _slaService.CalculateSlaExpiryAsync(defaultProfile.Id, DateTime.UtcNow);
                }
            }
            else
            {
                ticket.WorkflowProfileId = null;
                ticket.SlaExpiresAt = null;
            }
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
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> UpdateWorkflowState(Guid id, [FromBody] UpdateWorkflowStateRequest request)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!scope.HasGlobalAccess
            && !scope.AllowedClientIds.Contains(ticket.ClientId)
            && !(ticket.SiteId.HasValue && scope.AllowedSiteIds.Contains(ticket.SiteId.Value)))
        {
            return NotFound();
        }

        var changedBy = HttpContext.Items["UserId"] as Guid?;
        var updatedTicket = await _ticketWorkflowService.TransitionAsync(id, request.WorkflowStateId, changedBy, HttpContext.RequestAborted);

        return Ok(new { message = "Workflow state updated", ticket = updatedTicket });
    }

    // --- Comments ---

    [HttpGet("{id:guid}/comments")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetComments(
        Guid id,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        var items = await _repo.GetCommentsPageAsync(id, cursor, Math.Clamp(limit, 1, 200));
        var slice = CursorPaginationHelper.SlicePage(items.ToList(), Math.Clamp(limit, 1, 200));
        var nextCursor = slice.HasMore && slice.LastItem is not null
            ? CursorPaginationHelper.EncodeCreatedAtCursor(slice.LastItem.CreatedAt, slice.LastItem.Id)
            : null;

        return Ok(new CursorPageDto<TicketComment>(
            slice.Page,
            slice.Page.Count,
            cursor,
            nextCursor,
            slice.HasMore,
            Math.Clamp(limit, 1, 200)));
    }

    [HttpPost("{id:guid}/comments")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
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
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAttachments(
        Guid id,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null)
            return NotFound();

        var attachments = await _attachmentService.GetAttachmentsForEntityAsync("Ticket", id);
        var safeLimit = Math.Clamp(limit, 1, 200);

        // Se houver cursor, filtra em memória (poucos attachments por ticket)
        if (!string.IsNullOrWhiteSpace(cursor)
            && CursorPaginationHelper.TryDecodeCreatedAtCursor(cursor, out var cursorCreatedAtUtc, out var cursorId))
        {
            attachments = attachments
                .Where(a => a.CreatedAt < cursorCreatedAtUtc
                    || (a.CreatedAt == cursorCreatedAtUtc && a.Id.CompareTo(cursorId) < 0))
                .ToList();
        }

        var ordered = attachments
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .ToList();

        var page = ordered.Take(safeLimit).ToList();
        var hasMore = ordered.Count > safeLimit;
        var nextCursor = hasMore && page.Count > 0
            ? CursorPaginationHelper.EncodeCreatedAtCursor(page[^1].CreatedAt, page[^1].Id)
            : null;

        return Ok(new
        {
            items = page,
            cursor = nextCursor,
            hasMore
        });
    }

    [HttpPost("{id:guid}/attachments/presigned-upload")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
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
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
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

    // ── Reopen / Rating ──

    [HttpPost("{id:guid}/reopen")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Reopen(Guid id, [FromBody] ReopenTicketRequest request)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!scope.HasGlobalAccess
            && !scope.AllowedClientIds.Contains(ticket.ClientId)
            && !(ticket.SiteId.HasValue && scope.AllowedSiteIds.Contains(ticket.SiteId.Value)))
            return NotFound();

        if (!ticket.ClosedAt.HasValue)
            return BadRequest(new { error = "Ticket não está fechado. Use a transição de estado normal." });

        var initialState = await _workflowRepo.GetInitialStateAsync(ticket.ClientId);
        if (initialState is null)
            return BadRequest("No initial workflow state configured.");

        DateTime? newSla = null;
        if (ticket.WorkflowProfileId.HasValue)
            newSla = await _slaService.CalculateSlaExpiryAsync(ticket.WorkflowProfileId.Value, DateTime.UtcNow);

        ticket.WorkflowStateId = initialState.Id;
        ticket.ClosedAt = null;
        ticket.SlaBreached = false;
        ticket.SlaExpiresAt = newSla;
        ticket.SlaPausedSeconds = 0;
        ticket.SlaHoldStartedAt = null;
        ticket.FirstRespondedAt = null;
        ticket.SlaFirstResponseExpiresAt = null;
        ticket.UpdatedAt = DateTime.UtcNow;

        await _repo.UpdateAsync(ticket);

        await _activityLogService.LogActivityAsync(
            id, TicketActivityType.Reopened, null, null, null,
            request.Reason ?? "Ticket reaberto");

        if (ticket.AssignedToUserId.HasValue)
        {
            await _notificationService.PublishAsync(new NotificationPublishRequest(
                EventType: "ticket.reopened", Topic: "tickets",
                Title: "Ticket reaberto",
                Message: $"O ticket #{id} '{ticket.Title}' foi reaberto." + (string.IsNullOrWhiteSpace(request.Reason) ? "" : $" Motivo: {request.Reason}"),
                Severity: NotificationSeverity.Warning,
                Payload: new { ticketId = id },
                RecipientUserId: ticket.AssignedToUserId
            ));
        }

        return Ok(ticket);
    }

    [HttpPost("{id:guid}/rating")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Rate(Guid id, [FromBody] RateTicketRequest request)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!scope.HasGlobalAccess
            && !scope.AllowedClientIds.Contains(ticket.ClientId)
            && !(ticket.SiteId.HasValue && scope.AllowedSiteIds.Contains(ticket.SiteId.Value)))
            return NotFound();

        if (!ticket.ClosedAt.HasValue)
            return BadRequest(new { error = "Apenas tickets fechados podem ser avaliados." });

        ticket.Rating = request.Rating;
        ticket.RatedAt = DateTime.UtcNow;
        ticket.RatedBy = HttpContext.Items["Username"] as string ?? HttpContext.Items["UserId"]?.ToString();
        await _repo.UpdateAsync(ticket);

        await _activityLogService.LogActivityAsync(
            id, TicketActivityType.DescriptionUpdated, null, null, $"rating:{request.Rating}",
            $"Ticket avaliado com nota {request.Rating}" + (string.IsNullOrWhiteSpace(request.Comment) ? "" : $": {request.Comment}"));

        return Ok(new { ticketId = id, rating = request.Rating });
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
    Guid? AssignedToUserId,
    Dictionary<Guid, string>? CustomFieldValues = null);

public record UpdateTicketRequest(
    string Title,
    string Description,
    TicketPriority Priority,
    Guid? AssignedToUserId,
    string? Category,
    Guid? DepartmentId);

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
    string? UploadedBy = null);

public record ReopenTicketRequest(string? Reason);

public record RateTicketRequest(int Rating, string? Comment);
