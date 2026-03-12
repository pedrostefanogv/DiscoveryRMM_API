using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

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

    public TicketsController(
        ITicketRepository repo,
        IWorkflowRepository workflowRepo,
        IDepartmentRepository departmentRepo,
        IWorkflowProfileRepository workflowProfileRepo,
        ISlaService slaService,
        IActivityLogService activityLogService,
        IAttachmentService attachmentService,
        IServerConfigurationRepository serverConfigurationRepository)
    {
        _repo = repo;
        _workflowRepo = workflowRepo;
        _departmentRepo = departmentRepo;
        _workflowProfileRepo = workflowProfileRepo;
        _slaService = slaService;
        _activityLogService = activityLogService;
        _attachmentService = attachmentService;
        _serverConfigurationRepository = serverConfigurationRepository;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? workflowStateId, [FromQuery] int limit = 100, [FromQuery] int offset = 0)
    {
        var tickets = await _repo.GetAllAsync(workflowStateId, limit, offset);
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

        var ticket = new Ticket
        {
            ClientId = request.ClientId,
            SiteId = request.SiteId,
            AgentId = request.AgentId,
            DepartmentId = request.DepartmentId,
            WorkflowProfileId = request.WorkflowProfileId,
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
        if (newState?.IsFinal == true)
            ticket.ClosedAt = DateTime.UtcNow;

        await _repo.UpdateWorkflowStateAsync(id, request.WorkflowStateId);

        // Log da mudança de estado
        await _activityLogService.LogStateChangeAsync(id, null, oldStateId, request.WorkflowStateId);

        // Recarregar o ticket atualizado do banco
        var updatedTicket = await _repo.GetByIdAsync(id);

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
