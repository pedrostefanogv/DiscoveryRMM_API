using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.AiChat;
using Discovery.Core.Cqrs.AgentAuth.Automation;
using Discovery.Core.Cqrs.AgentAuth.Configuration;
using Discovery.Core.Cqrs.AgentAuth.Hardware;
using Discovery.Core.Cqrs.AgentAuth.Knowledge;
using Discovery.Core.Cqrs.AgentAuth.Misc;
using Discovery.Core.Cqrs.AgentAuth.P2P;
using Discovery.Core.Cqrs.AgentAuth.Software;
using Discovery.Core.Cqrs.AgentAuth.Status;
using Discovery.Core.Cqrs.AgentAuth.Tickets;
using Discovery.Core.Cqrs.Notes.Commands;
using Discovery.Core.Cqrs.Configurations.Queries;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using MediatR;
using Discovery.Api.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-auth")]
[AllowAnonymous]
public class AgentAuthController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IAgentRepository _agentRepo;
    private readonly ISiteRepository _siteRepo;
    private readonly IWorkflowRepository _workflowRepo;
    private readonly IAiChatService _aiChat;
    private readonly IDepartmentRepository _departmentRepo;
    private readonly IWorkflowProfileRepository _workflowProfileRepo;
    private readonly IDepartmentCustomFieldService _departmentCustomFieldService;
    private readonly ITicketFormSchemaService _ticketFormSchemaService;
    private readonly IP2pService _p2pService;
    private readonly IAttachmentService _attachmentService;
    private readonly IAttachmentRepository _attachmentRepository;
    private readonly IKnowledgeArticleRepository _knowledgeArticleRepository;
    private readonly INotificationService _notificationService;
    private readonly ILogger<AgentAuthController> _logger;

    public AgentAuthController(
        IMediator mediator,
        IAgentRepository agentRepo,
        ISiteRepository siteRepo,
        IWorkflowRepository workflowRepo,
        IAiChatService aiChat,
        IDepartmentRepository departmentRepo,
        IWorkflowProfileRepository workflowProfileRepo,
        IDepartmentCustomFieldService departmentCustomFieldService,
        ITicketFormSchemaService ticketFormSchemaService,
        IP2pService p2pService,
        IAttachmentService attachmentService,
        IAttachmentRepository attachmentRepository,
        IKnowledgeArticleRepository knowledgeArticleRepository,
        INotificationService notificationService,
        ILogger<AgentAuthController> logger)
    {
        _mediator = mediator;
        _agentRepo = agentRepo;
        _siteRepo = siteRepo;
        _workflowRepo = workflowRepo;
        _aiChat = aiChat;
        _departmentRepo = departmentRepo;
        _workflowProfileRepo = workflowProfileRepo;
        _departmentCustomFieldService = departmentCustomFieldService;
        _ticketFormSchemaService = ticketFormSchemaService;
        _p2pService = p2pService;
        _attachmentService = attachmentService;
        _attachmentRepository = attachmentRepository;
        _knowledgeArticleRepository = knowledgeArticleRepository;
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <summary>B5: limite de toolResults aceitos por request (anti-abuso).</summary>
    private const int MaxToolResultsPerRequest = 20;

    /// <summary>
    /// Payload de fechamento/avaliação. Aceita "feedback" (portal), "comment"
    /// (support do agente) e "notes" (versões antigas do agente Go).
    /// </summary>
    public record CloseAndRateTicketRequest(
        int? Rating,
        string? Feedback = null,
        string? Comment = null,
        string? Notes = null,
        Guid? WorkflowStateId = null);

    /// <summary>Payload de reabertura pelo agent (motivo opcional).</summary>
    public record ReopenMyTicketRequest(string? Reason = null);

    /// <summary>Payload de avaliação (CSAT) pelo agent.</summary>
    public record RateMyTicketRequest(int Rating, string? Feedback = null);

    // ── Auth Helpers ──────────────────────────────────────────────────────

    private bool TryGetAgentId(out Guid agentId)
    {
        if (HttpContext.Items["AgentId"] is Guid id) { agentId = id; return true; }
        agentId = Guid.Empty; return false;
    }

    private async Task<(Agent? agent, IActionResult? blocked)> GetAgentOrBlockAsync(Guid agentId, bool allowPending)
    {
        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null) return (null, NotFound(new { error = "Agent not found." }));
        if (!allowPending && agent.ZeroTouchPending) return (null, StatusCode(403, new { error = "Agent registration is pending (zero-touch)." }));
        return (agent, null);
    }

    private IActionResult MapResult<T>(Result<T> result, Func<T, IActionResult> onSuccess)
        where T : notnull
        => result.Match(onSuccess, errors => errors[0].Code switch
        {
            "NotFound" => NotFound(new { error = errors[0].Message }),
            "Forbidden" => StatusCode(403, new { error = errors[0].Message }),
            "Validation" => BadRequest(new { error = errors[0].Message, field = errors[0].Field }),
            "Internal" => StatusCode(500, new { error = "Erro interno do servidor" }),
            _ => BadRequest(new { error = errors[0].Message })
        });

    // ── Hardware ──────────────────────────────────────────────────────────

    [HttpGet("me/hardware")]
    public async Task<IActionResult> GetHardware()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentHardwareQuery(id)), Ok);
    }

    // M-fix (413 homologação 13/09): o payload de hardware embute o inventário
    // bruto em inventoryRaw (JSON escapado — tamanho 2-4x o original) e pode
    // passar do default do Kestrel (~28,6 MB) com inventários grandes.
    // Limite explícito de 64 MB alinhado ao client_max_body_size 100m do nginx.
    [RequestSizeLimit(64_000_000)]
    [HttpPost("me/hardware")]
    public async Task<IActionResult> ReportHardwarePost([FromBody] ReportAgentHardwareCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }), _ => Ok());
    }

    [HttpPut("me/hardware")]
    public Task<IActionResult> ReportHardwarePut([FromBody] ReportAgentHardwareCommand cmd) => ReportHardwarePost(cmd);

    // ── Software ──────────────────────────────────────────────────────────

    [HttpGet("me/software")]
    public async Task<IActionResult> GetSoftwareInventory()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentSoftwareQuery(id)), Ok);
    }

    [HttpPost("me/software")]
    public async Task<IActionResult> ReportSoftwarePost([FromBody] ReportAgentSoftwareCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }), _ => Ok(new { Message = "Software inventory updated." }));
    }

    [HttpPut("me/software")]
    public Task<IActionResult> ReportSoftwarePut([FromBody] ReportAgentSoftwareCommand cmd) => ReportSoftwarePost(cmd);

    // ── Status ────────────────────────────────────────────────────────────

    [HttpGet("me/realtime/status")]
    public async Task<IActionResult> GetAgentRealtimeStatus()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        return MapResult(await _mediator.Send(new GetAgentRealtimeStatusQuery()), Ok);
    }

    // ── Automation ────────────────────────────────────────────────────────

    [HttpPost("me/automation/policy-sync")]
    public async Task<IActionResult> SyncAutomationPolicy([FromBody] SyncAutomationPolicyCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }, ct), Ok);
    }

    [HttpGet("me/commands")]
    public async Task<IActionResult> GetCommands([FromQuery] int limit = 50)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentCommandsQuery(id, limit)), Ok);
    }

    [HttpPost("me/automation/executions/{commandId:guid}/ack")]
    public async Task<IActionResult> AckAutomationExecution(Guid commandId, [FromBody] AckAutomationExecutionCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id, CommandId = commandId }), _ => Ok(new { acknowledged = true, commandId }));
    }

    [HttpPost("me/automation/executions/{commandId:guid}/result")]
    public async Task<IActionResult> CompleteAutomationExecution(Guid commandId, [FromBody] CompleteAutomationExecutionCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id, CommandId = commandId }), _ => Ok(new { completed = true, commandId }));
    }

    // ── Configuration ─────────────────────────────────────────────────────

    [HttpGet("me/configuration")]
    public async Task<IActionResult> GetConfiguration()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentConfigurationQuery(id)), Ok);
    }

    [HttpPost("me/tls-mismatch")]
    public async Task<IActionResult> ReportTlsMismatch([FromBody] ReportAgentTlsMismatchCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out _)) return Unauthorized();
        return MapResult(await _mediator.Send(cmd, ct), Ok);
    }

    [HttpGet("me/sync-manifest")]
    public async Task<IActionResult> GetSyncManifest(CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentSyncManifestQuery(id), ct), Ok);
    }

    // ── Tickets ───────────────────────────────────────────────────────────

    [HttpGet("me/tickets")]
    public async Task<IActionResult> GetMyTickets([FromQuery] Guid? workflowStateId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketsQuery(id, workflowStateId)), Ok);
    }

    [HttpGet("me/tickets/{ticketId:guid}")]
    public async Task<IActionResult> GetMyTicket(Guid ticketId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketQuery(id, ticketId)), Ok);
    }

    /// <summary>
    /// Campos personalizados do departamento do chamado com os valores gravados,
    /// para o detalhe do agent (somente leitura; campos internos ficam de fora).
    /// </summary>
    [HttpGet("me/tickets/{ticketId:guid}/fields")]
    public async Task<IActionResult> GetMyTicketFields(Guid ticketId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketFieldsQuery(id, ticketId)), Ok);
    }

    /// <summary>
    /// Respostas do mini questionário do template do chamado (somente leitura).
    /// </summary>
    [HttpGet("me/tickets/{ticketId:guid}/answers")]
    public async Task<IActionResult> GetMyTicketAnswers(Guid ticketId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketAnswersQuery(id, ticketId)), Ok);
    }

    [HttpGet("me/ticket-templates")]
    public async Task<IActionResult> GetMyTicketTemplates()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketTemplatesQuery(id)), Ok);
    }

    [HttpPost("me/tickets")]
    [IdempotencyFilter]
    public async Task<IActionResult> CreateMyTicket([FromBody] CreateMyTicketCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }), dto => CreatedAtAction(nameof(GetMyTicket), new { ticketId = (dto as dynamic)?.Id }, dto));
    }

    [HttpPost("me/tickets/{ticketId:guid}/comments")]
    [IdempotencyFilter]
    public async Task<IActionResult> AddMyTicketComment(Guid ticketId, [FromBody] AddMyTicketCommentCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id, TicketId = ticketId }), Ok);
    }

    [HttpGet("me/tickets/{ticketId:guid}/comments")]
    public async Task<IActionResult> GetMyTicketComments(Guid ticketId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketCommentsQuery(id, ticketId)), Ok);
    }


    // ── Anexos de chamados (agent) ─────────────────────────────────────────
    //
    // O agent precisa anexar arquivos/prints ao comentar um chamado. As rotas
    // do console (/tickets/{id}/attachments/*) exigem permissao de usuario,
    // entao o equivalente existe aqui, com isolamento por Ticket.AgentId (IDOR)
    // e as MESMAS regras de TicketAttachmentSettings usadas no console/chat.

    /// <summary>Configuracao de anexos (tipos/tamanho) para a UI do agent.</summary>
    [HttpGet("me/tickets/attachment-settings")]
    public async Task<IActionResult> GetMyTicketAttachmentSettings()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var result = await _mediator.Send(new GetTicketAttachmentSettingsQuery(), HttpContext.RequestAborted);
        if (result.IsFailure || result.Value is null)
            return BadRequest(new { error = "Não foi possível ler a configuração de anexos." });

        var settings = result.Value;
        return Ok(new
        {
            enabled = settings.Enabled,
            allowedContentTypes = settings.AllowedContentTypes,
            maxFileSizeBytes = settings.MaxFileSizeBytes,
        });
    }

    /// <summary>Lista os anexos do chamado (somente do proprio agent).</summary>
    [HttpGet("me/tickets/{ticketId:guid}/attachments")]
    public async Task<IActionResult> GetMyTicketAttachments(
        Guid ticketId,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        if (await ResolveMyTicketAsync(id, ticketId) is null) return NotFound();

        var result = await _mediator.Send(new GetTicketAttachmentsQuery(ticketId, cursor, limit), HttpContext.RequestAborted);
        return MapResult(result, Ok);
    }

    /// <summary>Lista consolidada e paginada (repositório + conversa) do agent.</summary>
    [HttpGet("me/tickets/{ticketId:guid}/attachments/all")]
    public async Task<IActionResult> GetMyTicketAttachmentsAll(
        Guid ticketId,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        if (await ResolveMyTicketAsync(id, ticketId) is null) return NotFound();

        var result = await _mediator.Send(
            new GetTicketAttachmentsAllQuery(ticketId, cursor, limit), HttpContext.RequestAborted);
        return MapResult(result, Ok);
    }

    /// <summary>Baixa um anexo do chamado (stream same-origin autenticado).</summary>
    [HttpGet("me/tickets/{ticketId:guid}/attachments/{attachmentId:guid}/download")]
    public async Task<IActionResult> DownloadMyTicketAttachment(Guid ticketId, Guid attachmentId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        if (await ResolveMyTicketAsync(id, ticketId) is null) return NotFound();

        var attachment = await _attachmentRepository.GetByIdAsync(attachmentId, ct);
        if (attachment is null
            || attachment.IsDeleted
            || !string.Equals(attachment.EntityType, "Ticket", StringComparison.OrdinalIgnoreCase)
            || attachment.EntityId != ticketId)
        {
            return NotFound();
        }

        try
        {
            var stream = await _attachmentService.DownloadAttachmentAsync(attachmentId, ct);
            var contentType = string.IsNullOrWhiteSpace(attachment.ContentType)
                ? "application/octet-stream"
                : attachment.ContentType;
            return File(stream, contentType, attachment.FileName);
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível baixar o arquivo." });
        }
    }

    /// <summary>Prepara o upload direto no storage (URL pre-assinada).</summary>
    [HttpPost("me/tickets/{ticketId:guid}/attachments/presigned-upload")]
    public async Task<IActionResult> PrepareMyTicketAttachmentUpload(
        Guid ticketId,
        [FromBody] PresignedUploadRequestDto req)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var ticket = await ResolveMyTicketAsync(id, ticketId);
        if (ticket is null) return NotFound();

        var settingsResult = await _mediator.Send(new GetTicketAttachmentSettingsQuery(), HttpContext.RequestAborted);
        if (settingsResult.IsFailure || settingsResult.Value is null)
            return BadRequest(new { error = "Não foi possível ler a configuração de anexos." });
        var settings = settingsResult.Value;

        if (!settings.Enabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Anexos de chamados estão desabilitados nas configurações do servidor." });

        if (req is null || string.IsNullOrWhiteSpace(req.FileName) || string.IsNullOrWhiteSpace(req.ContentType))
            return BadRequest(new { error = "Nome e tipo do arquivo são obrigatórios." });

        if (!settings.IsContentTypeAllowed(req.ContentType))
            return BadRequest(new { error = $"Tipo de arquivo não permitido: {req.ContentType}." });

        if (req.SizeBytes <= 0 || req.SizeBytes > settings.MaxFileSizeBytes)
            return BadRequest(new { error = $"Tamanho de arquivo inválido. Máximo permitido: {settings.MaxFileSizeBytes} bytes." });

        var result = await _attachmentService.PreparePresignedUploadAsync(
            "Ticket", ticketId, ticket.ClientId, req.FileName, req.ContentType, req.SizeBytes,
            settings.PresignedUploadUrlTtlMinutes, HttpContext.RequestAborted);

        return Ok(result);
    }

    /// <summary>Finaliza o upload pre-assinado e persiste o anexo do chamado.</summary>
    [HttpPost("me/tickets/{ticketId:guid}/attachments/complete-upload")]
    public async Task<IActionResult> CompleteMyTicketAttachmentUpload(
        Guid ticketId,
        [FromBody] AgentCompleteUploadRequest req,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var ticket = await ResolveMyTicketAsync(id, ticketId);
        if (ticket is null) return NotFound();

        var settingsResult = await _mediator.Send(new GetTicketAttachmentSettingsQuery(), ct);
        if (settingsResult.IsFailure || settingsResult.Value is null)
            return BadRequest(new { error = "Não foi possível ler a configuração de anexos." });
        var settings = settingsResult.Value;

        if (!settings.Enabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Anexos de chamados estão desabilitados nas configurações do servidor." });

        if (req is null || !settings.IsContentTypeAllowed(req.ContentType))
            return BadRequest(new { error = $"Tipo de arquivo não permitido: {req?.ContentType}." });

        if (req.SizeBytes <= 0 || req.SizeBytes > settings.MaxFileSizeBytes)
            return BadRequest(new { error = $"Tamanho de arquivo inválido. Máximo permitido: {settings.MaxFileSizeBytes} bytes." });

        try
        {
            // Autor sempre derivado do agent autenticado (nunca do body).
            var attachment = await _attachmentService.CompletePresignedUploadAsync(
                req.AttachmentId, "Ticket", ticketId, ticket.ClientId,
                req.FileName, req.ContentType, req.SizeBytes, req.ObjectKey,
                $"agent:{id}", ct);

            // O limite vale sobre o tamanho REAL no storage (nao o declarado).
            if (attachment.SizeBytes > settings.MaxFileSizeBytes)
            {
                await _attachmentService.DeleteAttachmentAsync(attachment.Id, ct);
                return StatusCode(StatusCodes.Status413RequestEntityTooLarge, new { error = $"Arquivo excede o tamanho máximo permitido ({settings.MaxFileSizeBytes} bytes)." });
            }

            // Anexo de REPOSITORIO enviado pelo agent: avisa o console (badge).
            await NotifyRepositoryAttachmentAsync(id, ticket, attachment.Id, attachment.FileName, attachment.SizeBytes);

            return Ok(attachment);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>
    /// Notifica o console (badge no sino) que o agent anexou um arquivo ao
    /// repositorio do chamado. A notificacao vai para a chave compartilhada
    /// "tickets" (fila do time): o contrato publico do agent nao expoe o
    /// responsavel do chamado, e o payload identifica o chamado para o deep
    /// link. O binario nunca entra no payload.
    /// </summary>
    private async Task NotifyRepositoryAttachmentAsync(
        Guid agentId, AgentTicketDto ticket, Guid attachmentId, string fileName, long sizeBytes)
    {
        try
        {
            await _notificationService.PublishAsync(new NotificationPublishRequest(
                EventType: "ticket.attachment.added",
                Topic: "tickets",
                Title: "Novo anexo enviado pelo cliente",
                Message: $"{ticket.Title}: {fileName}",
                Severity: NotificationSeverity.Informational,
                Payload: new
                {
                    ticketId = ticket.Id,
                    attachmentId,
                    fileName,
                    sizeBytes,
                    origin = "agent"
                },
                RecipientKey: "tickets",
                CreatedBy: $"agent:{agentId}"));
        }
        catch (Exception ex)
        {
            // Best-effort: o upload ja esta persistido.
            _logger.LogWarning(ex, "Falha ao notificar anexo do chamado {TicketId}", ticket.Id);
        }
    }

    // ── Anexos de comentario (nivel conversa, no agent) ────────────────────
    //
    // Mesmo modelo do console: anexo de comentario e pequeno (10 MB por padrao,
    // imagens/PDF, com miniatura) e fica ligado ao COMENTARIO, nao ao chamado.
    // A posse e validada duas vezes: o chamado tem de ser do agent e o
    // comentario tem de pertencer a esse chamado.

    /// <summary>Limites/tipos do anexo de comentario para a UI do agent.</summary>
    [HttpGet("me/tickets/comment-attachment-settings")]
    public async Task<IActionResult> GetMyCommentAttachmentSettings()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var result = await _mediator.Send(new GetCommentAttachmentSettingsQuery(), HttpContext.RequestAborted);
        if (result.IsFailure || result.Value is null)
            return BadRequest(new { error = "Nao foi possivel ler a configuracao de anexos de comentario." });

        var settings = result.Value;
        return Ok(new
        {
            enabled = settings.Enabled,
            allowedContentTypes = settings.AllowedContentTypes,
            maxFileSizeBytes = settings.MaxFileSizeBytes,
        });
    }

    /// <summary>Valida chamado do agent + comentario pertencente a esse chamado.</summary>
    private async Task<bool> CommentBelongsToMyTicketAsync(Guid agentId, Guid ticketId, Guid commentId)
    {
        if (await ResolveMyTicketAsync(agentId, ticketId) is null) return false;

        var result = await _mediator.Send(new GetTicketCommentQuery(ticketId, commentId), HttpContext.RequestAborted);
        return result.IsSuccess && result.Value is not null;
    }

    /// <summary>Prepara o upload direto no storage para um anexo de comentario.</summary>
    [HttpPost("me/tickets/{ticketId:guid}/comments/{commentId:guid}/attachments/presigned-upload")]
    public async Task<IActionResult> PrepareMyCommentAttachmentUpload(
        Guid ticketId, Guid commentId, [FromBody] PresignedUploadRequestDto req)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        if (!await CommentBelongsToMyTicketAsync(id, ticketId, commentId)) return NotFound();
        var ticket = await ResolveMyTicketAsync(id, ticketId);
        if (ticket is null) return NotFound();

        var settingsResult = await _mediator.Send(new GetCommentAttachmentSettingsQuery(), HttpContext.RequestAborted);
        if (settingsResult.IsFailure || settingsResult.Value is null)
            return BadRequest(new { error = "Nao foi possivel ler a configuracao de anexos de comentario." });
        var settings = settingsResult.Value;

        if (req is null || string.IsNullOrWhiteSpace(req.FileName) || string.IsNullOrWhiteSpace(req.ContentType))
            return BadRequest(new { error = "Nome e tipo do arquivo sao obrigatorios." });

        if (!settings.IsContentTypeAllowed(req.ContentType))
            return BadRequest(new { error = $"Tipo de arquivo nao permitido em comentarios: {req.ContentType}." });

        if (req.SizeBytes <= 0 || req.SizeBytes > settings.MaxFileSizeBytes)
            return BadRequest(new { error = $"Anexo de comentario excede o limite de {settings.MaxFileSizeBytes} bytes." });

        var result = await _attachmentService.PreparePresignedUploadAsync(
            CommentEntityType, commentId, ticket.ClientId, req.FileName, req.ContentType,
            req.SizeBytes, settings.PresignedUploadUrlTtlMinutes, HttpContext.RequestAborted);

        return Ok(result);
    }

    /// <summary>Finaliza o upload pre-assinado e persiste o anexo do comentario.</summary>
    [HttpPost("me/tickets/{ticketId:guid}/comments/{commentId:guid}/attachments/complete-upload")]
    public async Task<IActionResult> CompleteMyCommentAttachmentUpload(
        Guid ticketId, Guid commentId, [FromBody] AgentCompleteUploadRequest req, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        if (!await CommentBelongsToMyTicketAsync(id, ticketId, commentId)) return NotFound();
        var ticket = await ResolveMyTicketAsync(id, ticketId);
        if (ticket is null) return NotFound();

        var settingsResult = await _mediator.Send(new GetCommentAttachmentSettingsQuery(), ct);
        if (settingsResult.IsFailure || settingsResult.Value is null)
            return BadRequest(new { error = "Nao foi possivel ler a configuracao de anexos de comentario." });
        var settings = settingsResult.Value;

        if (req is null || !settings.IsContentTypeAllowed(req.ContentType))
            return BadRequest(new { error = $"Tipo de arquivo nao permitido em comentarios: {req?.ContentType}." });

        if (req.SizeBytes <= 0 || req.SizeBytes > settings.MaxFileSizeBytes)
            return BadRequest(new { error = $"Anexo de comentario excede o limite de {settings.MaxFileSizeBytes} bytes." });

        try
        {
            // Autor sempre derivado do agent autenticado (nunca do body).
            var attachment = await _attachmentService.CompletePresignedUploadAsync(
                req.AttachmentId, CommentEntityType, commentId, ticket.ClientId,
                req.FileName, req.ContentType, req.SizeBytes, req.ObjectKey, $"agent:{id}", ct);

            if (attachment.SizeBytes > settings.MaxFileSizeBytes)
            {
                await _attachmentService.DeleteAttachmentAsync(attachment.Id, ct);
                return StatusCode(StatusCodes.Status413RequestEntityTooLarge,
                    new { error = $"Arquivo excede o limite de anexo de comentario ({settings.MaxFileSizeBytes} bytes)." });
            }

            return Ok(attachment);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Baixa um anexo de comentario do chamado do proprio agent.</summary>
    [HttpGet("me/tickets/{ticketId:guid}/comments/{commentId:guid}/attachments/{attachmentId:guid}/download")]
    public async Task<IActionResult> DownloadMyCommentAttachment(
        Guid ticketId, Guid commentId, Guid attachmentId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        if (!await CommentBelongsToMyTicketAsync(id, ticketId, commentId)) return NotFound();

        var attachment = await _attachmentRepository.GetByIdAsync(attachmentId, ct);
        if (attachment is null
            || attachment.IsDeleted
            || !string.Equals(attachment.EntityType, CommentEntityType, StringComparison.OrdinalIgnoreCase)
            || attachment.EntityId != commentId)
        {
            return NotFound();
        }

        try
        {
            var stream = await _attachmentService.DownloadAttachmentAsync(attachmentId, ct);
            var contentType = string.IsNullOrWhiteSpace(attachment.ContentType)
                ? "application/octet-stream"
                : attachment.ContentType;
            return File(stream, contentType, attachment.FileName);
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Nao foi possivel baixar o arquivo." });
        }
    }

    /// <summary>EntityType dos anexos de comentario na tabela attachments.</summary>
    private const string CommentEntityType = "TicketComment";

    /// <summary>
    /// Ticket do proprio agent (isolamento por AgentId). Devolve null quando o
    /// ticket nao existe OU pertence a outro agent — sem revelar a existencia.
    /// </summary>
    private async Task<AgentTicketDto?> ResolveMyTicketAsync(Guid agentId, Guid ticketId)
    {
        var result = await _mediator.Send(new GetMyTicketQuery(agentId, ticketId), HttpContext.RequestAborted);
        return result.IsSuccess ? result.Value : null;
    }
    /// <summary>
    /// Lista os estados de workflow disponíveis para os tickets do agente.
    /// O seletor de estado final da UI de suporte do agente chama este endpoint.
    /// </summary>
    [HttpGet("me/tickets/workflow-states")]
    public async Task<IActionResult> GetMyTicketWorkflowStates()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        Guid? clientId = null;
        if (agent!.SiteId != Guid.Empty)
        {
            var site = await _siteRepo.GetByIdAsync(agent.SiteId);
            clientId = site?.ClientId;
        }

        var states = await _workflowRepo.GetStatesAsync(clientId);
        return Ok(states.Select(s => new
        {
            s.Id,
            s.Name,
            s.Color,
            s.IsInitial,
            s.IsFinal,
            s.SortOrder
        }));
    }

    /// <summary>
    /// Opções para o formulário de abertura de chamado do agente (departamento e
    /// perfil de workflow), filtradas pelo cliente do site do agente. Alimenta o
    /// picker da UI de suporte; sem departamento o servidor não calcula SLA.
    /// </summary>
    [HttpGet("me/tickets/options")]
    public async Task<IActionResult> GetMyTicketOptions()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        Guid? clientId = null;
        if (agent!.SiteId != Guid.Empty)
        {
            var site = await _siteRepo.GetByIdAsync(agent.SiteId);
            clientId = site?.ClientId;
        }

        var departments = clientId.HasValue
            ? await _departmentRepo.GetByClientAsync(clientId.Value, includeGlobal: true, activeOnly: true)
            : new List<Discovery.Core.Entities.Department>();
        var profiles = await _workflowProfileRepo.GetByClientAsync(clientId, includeGlobal: true);

        return Ok(new
        {
            departments = departments.Select(d => new { d.Id, d.Name }).ToList(),
            workflowProfiles = profiles
                .Where(p => p.IsActive)
                .Select(p => new { p.Id, p.Name, p.DepartmentId })
                .ToList()
        });
    }

    /// <summary>
    /// Campos personalizados públicos de um departamento, para o formulário de
    /// abertura do agent. Os campos do departamento valem para TODO chamado do
    /// departamento — com ou sem template — e são validados pelo servidor.
    /// </summary>
    [HttpGet("me/tickets/departments/{departmentId:guid}/fields")]
    public async Task<IActionResult> GetMyDepartmentFields(Guid departmentId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        Guid? clientId = null;
        if (agent!.SiteId != Guid.Empty)
        {
            var site = await _siteRepo.GetByIdAsync(agent.SiteId);
            clientId = site?.ClientId;
        }

        // Escopo: departamento do próprio cliente ou global (mesma regra da
        // abertura). Não revela existência de departamentos de terceiros.
        var department = await _departmentRepo.GetByIdAsync(departmentId);
        if (department is null || (department.ClientId.HasValue && department.ClientId.Value != clientId))
            return NotFound(new { error = "Departamento não encontrado para este agente." });

        var fields = await _departmentCustomFieldService.GetPublicSchemaForDepartmentAsync(departmentId);
        return Ok(fields.Select(f => new
        {
            f.DefinitionId,
            f.Name,
            f.Label,
            f.Description,
            DataType = f.DataType.ToString(),
            f.IsRequired,
            f.Options,
            f.ValidationRegex,
            f.InputMask,
            f.MinLength,
            f.MaxLength,
            f.MinValue,
            f.MaxValue,
        }));
    }

    /// <summary>
    /// Schema do formulário de abertura de chamado do agent para um
    /// departamento (campos com opções: categoria e prioridade). O agent não tem
    /// lista fixa: renderiza exatamente o que este endpoint devolve, e as
    /// opções variam conforme os modelos e o histórico do departamento. Por isso
    /// o agent só chama este endpoint depois que o departamento é escolhido.
    /// </summary>
    [HttpGet("me/tickets/departments/{departmentId:guid}/form-schema")]
    public async Task<IActionResult> GetMyDepartmentFormSchema(Guid departmentId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        Guid? clientId = null;
        if (agent!.SiteId != Guid.Empty)
        {
            var site = await _siteRepo.GetByIdAsync(agent.SiteId);
            clientId = site?.ClientId;
        }

        // Mesma regra de escopo dos campos do departamento: departamento do
        // próprio cliente ou global. Não revela existência de terceiros.
        var department = await _departmentRepo.GetByIdAsync(departmentId);
        if (department is null || (department.ClientId.HasValue && department.ClientId.Value != clientId))
            return NotFound(new { error = "Departamento não encontrado para este agente." });

        var schema = await _ticketFormSchemaService.GetForDepartmentAsync(departmentId, clientId);
        return Ok(schema);
    }

    [HttpPost("me/tickets/{ticketId:guid}/close")]
    [IdempotencyFilter]
    public async Task<IActionResult> CloseAndRateTicket(Guid ticketId, [FromBody] CloseAndRateTicketRequest req)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var feedback = req.Feedback ?? req.Comment ?? req.Notes;
        var cmd = new CloseAndRateMyTicketCommand(id, ticketId, req.Rating, feedback, req.WorkflowStateId);
        return MapResult(await _mediator.Send(cmd), Ok);
    }

    /// <summary>
    /// Reabre um chamado encerrado vinculado ao agent. A UI usa para liberar
    /// novos comentários; a avaliação anterior é descartada pelo servidor.
    /// </summary>
    [HttpPost("me/tickets/{ticketId:guid}/reopen")]
    [IdempotencyFilter]
    public async Task<IActionResult> ReopenMyTicket(Guid ticketId, [FromBody] ReopenMyTicketRequest req)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var cmd = new ReopenMyTicketCommand(id, ticketId, req?.Reason);
        return MapResult(await _mediator.Send(cmd), dto => Ok(dto));
    }

    /// <summary>
    /// Avalia (CSAT 1..5 + feedback) um chamado encerrado vinculado ao agent.
    /// </summary>
    [HttpPost("me/tickets/{ticketId:guid}/rating")]
    [IdempotencyFilter]
    public async Task<IActionResult> RateMyTicket(Guid ticketId, [FromBody] RateMyTicketRequest req)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        // RatedByName = hostname da máquina: a UI exibe "Avaliado por: <PC>".
        // req pode ser nulo se o corpo vier "null"; a validação de 1..5 fica no
        // handler do portal (0 cai em erro de validação, não em 500).
        var cmd = new RateMyTicketCommand(id, ticketId, req?.Rating ?? 0, req?.Feedback, agent?.Hostname);
        return MapResult(await _mediator.Send(cmd), dto => Ok(dto));
    }

    // ── P2P ───────────────────────────────────────────────────────────────

    [HttpGet("me/p2p/seed-plan")]
    public async Task<IActionResult> GetP2pSeedPlan(CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentP2pSeedPlanQuery(id), ct), Ok);
    }

    [HttpPost("me/p2p/telemetry")]
    public async Task<IActionResult> IngestP2pTelemetry([FromBody] P2pTelemetryRequest request, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        // Rate limit server-side (5 req/10 min por agente). Estava implementado em
        // P2pService.CheckTelemetryRateLimitAsync mas nunca era chamado — o agente
        // tinha toda a lógica de retry para 429 que nunca acontecia.
        var retryAfterSeconds = await _p2pService.CheckTelemetryRateLimitAsync(id, ct);
        if (retryAfterSeconds > 0)
        {
            Response.Headers.RetryAfter = retryAfterSeconds.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "rate limit de telemetria excedido",
                code = "RATE_LIMIT_EXCEEDED"
            });
        }

        return MapResult(await _mediator.Send(new IngestP2pTelemetryCommand(id, request), ct), Ok);
    }

    // ── Knowledge ─────────────────────────────────────────────────────────

    [HttpGet("knowledge")]
    public async Task<IActionResult> GetKnowledgeArticles([FromQuery] string? category = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        // Paginação keyset: cursor opaco da página anterior (hasMore/nextCursor no envelope).
        return MapResult(await _mediator.Send(new GetKnowledgeArticlesQuery(id, category, cursor, limit), ct), Ok);
    }

    [HttpGet("knowledge/{articleId:guid}")]
    public async Task<IActionResult> GetKnowledgeArticle(Guid articleId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetKnowledgeArticleQuery(id, articleId), ct), Ok);
    }

    [HttpGet("knowledge/{articleId:guid}/pages")]
    public async Task<IActionResult> GetKnowledgeArticlePages(Guid articleId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetKnowledgeArticlePagesQuery(id, articleId), ct), Ok);
    }

    /// <summary>
    /// Bytes de uma imagem de artigo (o webview não tem o token do agente, então
    /// o download acontece no processo Go, que devolve data URL à UI).
    ///
    /// Diferente do anexo de chamado (isolado por Ticket.AgentId), aqui a
    /// autorização é a VISIBILIDADE DO ARTIGO: o agente só alcança imagem de
    /// artigo PUBLICADO no seu escopo (site → client → global), a mesma regra da
    /// listagem de artigos. Sem isso o id do anexo vira uma URL pública de fato.
    /// </summary>
    [HttpGet("knowledge/images/{attachmentId:guid}")]
    public async Task<IActionResult> GetKnowledgeImage(Guid attachmentId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        if (agent is null) return NotFound();

        var attachment = await _attachmentRepository.GetByIdAsync(attachmentId, ct);
        if (attachment is null
            || attachment.IsDeleted
            || !string.Equals(attachment.EntityType, KnowledgeImageRules.EntityType, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        // Leitura leve: aqui só importam status/escopo, não os chunks/embeddings.
        var article = await _knowledgeArticleRepository.GetWithoutChunksAsync(attachment.EntityId, ct);
        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (!KnowledgeArticleScope.IsVisibleToAgent(article, site?.ClientId, agent.SiteId))
            return NotFound();

        try
        {
            var stream = await _attachmentService.DownloadAttachmentAsync(attachmentId, ct);
            var contentType = string.IsNullOrWhiteSpace(attachment.ContentType)
                ? "application/octet-stream"
                : attachment.ContentType;

            Response.Headers["Cache-Control"] = "private, max-age=300";
            // Content-Type declarado no upload: nosniff impede interpretação
            // alternativa (ex.: poliglota servido como HTML).
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(stream, contentType);
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível baixar a imagem." });
        }
    }

    // ── Misc ──────────────────────────────────────────────────────────────

    [HttpGet("me")]
    public async Task<IActionResult> GetAgentIdentity()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentIdentityQuery(id)), Ok);
    }

    /// <summary>
    /// Política efetiva da app-store para o agent. Quando <paramref name="paged"/>
    /// é true a resposta é paginada (pageSize + cursor opaco + hasMore/nextCursor);
    /// sem ele, mantém-se a resposta única com a coleção completa para os agents
    /// já instalados.
    /// </summary>
    [HttpGet("me/app-store")]
    public async Task<IActionResult> GetAppStoreEffective(
        [FromQuery] string installationType = "Winget",
        [FromQuery] bool paged = false,
        [FromQuery] int pageSize = 0,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAppStoreEffectiveQuery(id, installationType, paged, pageSize, cursor), ct), Ok);
    }

    /// <summary>
    /// Baixa o instalador de um pacote custom publicado pelo servidor. Exige o
    /// token do agent, que o pacote exista no catalogo E que esteja liberado
    /// pela politica da loja para o escopo do agent (sem isso qualquer agent
    /// autenticado baixaria o instalador de um pacote nao aprovado).
    /// </summary>
    [HttpGet("me/app-store/custom/{packageId}/file")]
    public async Task<IActionResult> DownloadCustomAppFile(
        string packageId,
        [FromServices] IAppPackageRepository appPackages,
        [FromServices] ICustomPackageAssetService assets,
        [FromServices] IAppStoreService appStoreService,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        if (agent is null) return Unauthorized();

        var package = await appPackages.GetByInstallationTypeAndPackageIdAsync(
            AppInstallationType.Custom, packageId, ct);

        if (package is null || string.IsNullOrWhiteSpace(package.FileObjectKey))
            return NotFound(new { message = "Instalador nao encontrado para este pacote custom." });

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        var approved = await appStoreService.IsPackageApprovedAsync(
            site?.ClientId, agent.SiteId, agent.Id, AppInstallationType.Custom, package.PackageId, ct);

        if (!approved)
            return NotFound(new { message = "Pacote custom nao aprovado para este agent." });

        try
        {
            // Stream direto para o corpo da resposta: o arquivo pode ter GBs e
            // NÃO pode ser bufferizado em memória (causava OOM kill da API).
            var contentType = string.IsNullOrWhiteSpace(package.FileContentType)
                ? "application/octet-stream"
                : package.FileContentType;
            var fileName = ResolveCustomInstallerFileName(package.FileName, package.FileObjectKey, package.PackageId);

            Response.ContentType = contentType;
            Response.Headers["Content-Disposition"] =
                $"attachment; filename=\"{fileName.Replace("\"", string.Empty)}\"";
            Response.Headers["Cache-Control"] = "no-store";

            await assets.WriteToAsync(package.FileObjectKey, Response.Body, ct);
            return new EmptyResult();
        }
        catch (Exception) when (!Response.HasStarted)
        {
            return NotFound(new { message = "Arquivo nao encontrado no object storage." });
        }
    }

    /// <summary>
    /// Resolve o nome do instalador enviado ao agent. Pacotes custom sem
    /// <c>file_name</c> cadastrado caíam no fallback pelo packageId, devolvendo
    /// um arquivo SEM extensão — e o agent não consegue executá-lo (`cmd /C`
    /// recusa binário sem extensão, mesmo sendo um PE válido). A extensão do
    /// objeto no storage é usada como pista (o upload já valida a extensão) e o
    /// packageId só permanece cru quando não há nenhuma informação.
    /// </summary>
    internal static string ResolveCustomInstallerFileName(string? fileName, string? fileObjectKey, string packageId)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Trim());
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(Path.GetExtension(name)))
            return name;

        var baseName = string.IsNullOrWhiteSpace(name) ? packageId : name;
        var keyExtension = Path.GetExtension(fileObjectKey ?? string.Empty);
        return string.IsNullOrWhiteSpace(keyExtension) ? baseName : baseName + keyExtension;
    }

    [HttpGet("me/custom-fields/runtime")]
    public async Task<IActionResult> GetRuntimeCustomFields([FromQuery] Guid? taskId = null, [FromQuery] Guid? scriptId = null, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetRuntimeCustomFieldsQuery(id, taskId, scriptId), ct), Ok);
    }

    [HttpPost("me/custom-fields/collected")]
    public async Task<IActionResult> UpsertCollectedCustomField([FromBody] AgentCollectedCustomFieldValueRequest request, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new UpsertCollectedCustomFieldCommand(id, request), ct), Ok);
    }

    /// <summary>
    /// Cria uma nota no próprio agent. Usado por tarefas com "gravar saída como
    /// nota" para guardar o resultado/coleta no histórico do agente.
    /// </summary>
    [HttpPost("me/notes")]
    public async Task<IActionResult> CreateAgentNote([FromBody] CreateNoteRequest request, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var cmd = new CreateNoteCommand(null, null, id, request.Content, "agent", request.IsPinned);
        return MapResult(await _mediator.Send(cmd, ct), Ok);
    }

    [HttpPost("me/zero-touch/deploy-token")]
    public async Task<IActionResult> IssueZeroTouchDeployToken()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new IssueZeroTouchDeployTokenCommand(id)), Ok);
    }

    [HttpGet("me/update/manifest")]
    public async Task<IActionResult> GetAgentUpdateManifest(
        [FromQuery] string? currentVersion = null, [FromQuery] string? platform = null,
        [FromQuery] string? architecture = null, [FromQuery] string? artifactType = null, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentUpdateManifestQuery(id, currentVersion, platform, architecture, artifactType), ct), Ok);
    }

    [HttpGet("me/update/download")]
    public async Task<IActionResult> DownloadAgentUpdate(
        [FromQuery] Guid? releaseId = null, [FromQuery] string? version = null,
        [FromQuery] string? platform = null, [FromQuery] string? architecture = null,
        [FromQuery] string? artifactType = null, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new DownloadAgentUpdateQuery(id, releaseId, version, platform, architecture, artifactType), ct), Ok);
    }

    [HttpPost("me/update/report")]
    public async Task<IActionResult> ReportAgentUpdate([FromBody] AgentUpdateReportRequest report, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new ReportAgentUpdateCommand(id, report), ct), Ok);
    }

    // ── AI Chat ───────────────────────────────────────────────────────────

    [HttpPost("me/ai-chat")]
    public async Task<IActionResult> ChatSync([FromBody] ChatSyncCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id, ClientIp = ResolveAgentClientIp() }, ct), Ok);
    }

    [HttpPost("me/ai-chat/async")]
    public async Task<IActionResult> ChatAsync([FromBody] ChatAsyncCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }, ct), dto => Accepted(dto));
    }

    [HttpPost("me/ai-chat/stream")]
    public async Task ChatStream([FromBody] ChatStreamCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var agentId))
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "Agent not authenticated." }, ct);
            return;
        }
        var (_, blocked) = await GetAgentOrBlockAsync(agentId, false);
        if (blocked is not null)
        {
            var obj = (ObjectResult)blocked;
            HttpContext.Response.StatusCode = obj.StatusCode ?? 403;
            await HttpContext.Response.WriteAsJsonAsync(obj.Value ?? new { error = "Agent blocked." }, ct);
            return;
        }

        HttpContext.Response.ContentType = "text/event-stream; charset=utf-8";
        HttpContext.Response.Headers.Append("Cache-Control", "no-cache");
        HttpContext.Response.Headers.Append("Connection", "keep-alive");
        HttpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        // Preenche o ClientIp do comando (antes era campo morto no contrato).
        cmd = cmd with { ClientIp = ResolveAgentClientIp() };

        try
        {
            // ToolResults != null → round 2+ (agent executou tools)
            // Message != null → round 1 (nova mensagem do usuário)
            var sessionGuid = Guid.TryParse(cmd.SessionId, out var g) ? g : (Guid?)null;
            var toolResults = cmd.ToolResults?.Select(tr => new ToolResultItem(
                tr.CallId,
                tr.Name,
                TruncateToolResult(tr.Name, tr.Result))).ToList();

            // B5: limita a contagem de toolResults por request (o teto por item
            // é 32 KB, mas N itens podiam estourar o contexto/custo do LLM).
            if (toolResults is { Count: > MaxToolResultsPerRequest })
            {
                toolResults = toolResults!.Take(MaxToolResultsPerRequest).ToList();
            }

            // B4: ToolResults presentes → SEMPRE multi-round, independente de
            // Mode ou de Message (antes, {message:"", toolResults:[...]} caía no
            // branch legado e os toolResults eram descartados silenciosamente).
            IAsyncEnumerable<AiChatStreamChunk> stream;
            if (toolResults is { Count: > 0 })
            {
                stream = _aiChat.StreamMultiRoundAsync(agentId,
                    string.IsNullOrWhiteSpace(cmd.Message) ? null : cmd.Message,
                    sessionGuid, toolResults, cmd.DepartmentId, cmd.SystemNote, ct);
            }
            else
            {
                stream = _aiChat.StreamAsync(agentId, cmd.Message ?? string.Empty,
                    sessionGuid, cmd.DepartmentId, cmd.SystemNote, cmd.Images, ct);
            };

            await foreach (var chunk in stream)
            {
                if (chunk.Type == "error")
                {
                    await WriteSseJsonAsync(HttpContext, new { type = "error", error = chunk.Error ?? "unknown" }, ct);
                    await HttpContext.Response.Body.FlushAsync(ct);
                    return;
                }
                await WriteSseJsonAsync(HttpContext, chunk, ct);
                await HttpContext.Response.Body.FlushAsync(ct);
                if (chunk.Type is "done" or "round_end") break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cliente desconectou — nada a fazer.
        }
        catch (OperationCanceledException ex)
        {
            // B16: cancelamento do lado do provedor (timeout de geração). Antes era
            // engolido em silêncio e o cliente recebia HTTP 200 sem nenhum evento.
            _logger.LogWarning(ex, "[ChatStream] Geração cancelada antes de concluir (timeout do provedor?). TraceId={TraceId}", HttpContext.TraceIdentifier);
            await WriteSseJsonAsync(HttpContext, new { type = "error", error = "A geração da resposta excedeu o tempo limite. Tente novamente." }, CancellationToken.None);
            await HttpContext.Response.Body.FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                // B7: não expor exceções internas (DB/provedor/paths) ao client.
                // Log completo + mensagem genérica com traceId.
                var traceId = HttpContext.TraceIdentifier;
                _logger.LogError(ex, "[ChatStream] Exceção não tratada. TraceId={TraceId}", traceId);
                await WriteSseJsonAsync(HttpContext,
                    new { type = "error", error = "Erro interno ao processar o chat.", traceId }, ct);
            }
        }
    }

    [HttpPost("me/agent-tools/registry")]
    public async Task<IActionResult> RegisterAgentTools([FromBody] RegisterAgentToolsCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var agentId)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(agentId, false);
        if (blocked is not null) return blocked;

        var tools = cmd.Tools.Select(t => new AgentToolRegistration(
            t.Name, t.Description, t.ParametersSchema.GetRawText())).ToList();
        await _aiChat.RegisterAgentToolsAsync(agentId, agent!.SiteId, tools, ct);
        return Ok(new { registered = tools.Count });
    }

    /// <summary>
    /// Ranges IPv4/IPv6 oficiais da Cloudflare (https://www.cloudflare.com/ips/).
    /// Espelha os defaults de ForwardedHeadersOptions em Program.cs — o header
    /// CF-Connecting-IP só é aceito quando a conexão vem de um desses ranges.
    /// </summary>
    private static readonly System.Net.IPNetwork[] CloudflareNetworks =
    [
        new(System.Net.IPAddress.Parse("173.245.48.0"), 20),
        new(System.Net.IPAddress.Parse("103.21.244.0"), 22),
        new(System.Net.IPAddress.Parse("103.22.200.0"), 22),
        new(System.Net.IPAddress.Parse("103.31.4.0"), 22),
        new(System.Net.IPAddress.Parse("141.101.64.0"), 18),
        new(System.Net.IPAddress.Parse("108.162.192.0"), 18),
        new(System.Net.IPAddress.Parse("190.93.240.0"), 20),
        new(System.Net.IPAddress.Parse("188.114.96.0"), 20),
        new(System.Net.IPAddress.Parse("197.234.240.0"), 22),
        new(System.Net.IPAddress.Parse("198.41.128.0"), 17),
        new(System.Net.IPAddress.Parse("162.158.0.0"), 15),
        new(System.Net.IPAddress.Parse("104.16.0.0"), 13),
        new(System.Net.IPAddress.Parse("104.24.0.0"), 14),
        new(System.Net.IPAddress.Parse("172.64.0.0"), 13),
        new(System.Net.IPAddress.Parse("131.0.72.0"), 22),
        new(System.Net.IPAddress.Parse("2400:cb00::"), 32),
        new(System.Net.IPAddress.Parse("2606:4700::"), 32),
        new(System.Net.IPAddress.Parse("2803:f800::"), 32),
        new(System.Net.IPAddress.Parse("2405:b500::"), 32),
        new(System.Net.IPAddress.Parse("2405:8100::"), 32),
        new(System.Net.IPAddress.Parse("2a06:98c0::"), 29),
        new(System.Net.IPAddress.Parse("2c0f:f248::"), 32),
    ];

    /// <summary>
    /// Verifica se o IP remoto pertence a um range da Cloudflare.
    /// </summary>
    private static bool IsCloudflareIp(System.Net.IPAddress? remoteIp)
    {
        if (remoteIp is null) return false;
        foreach (var net in CloudflareNetworks)
        {
            if (net.Contains(remoteIp)) return true;
        }
        return false;
    }

    /// <summary>
    /// Resolve o IP real do agent de forma confiável:
    /// 1. Connection.RemoteIpAddress — já resolvido pelo middleware
    ///    ForwardedHeaders (X-Forwarded-For aceito apenas de proxies confiáveis:
    ///    ranges Cloudflare + localhost, configurados em Program.cs).
    /// 2. CF-Connecting-IP — aceito SOMENTE se a conexão direta veio de um IP
    ///    Cloudflare (impede spoof por clients autenticados).
    /// 3. Fallback: RemoteIpAddress cru.
    /// Nunca lê X-Forwarded-For manualmente (spoofável pelo agent).
    /// </summary>
    private string ResolveAgentClientIp()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;

        // 1. ForwardedHeaders middleware já resolveu o IP real de proxy confiável.
        if (remoteIp is not null && !IsCloudflareIp(remoteIp) && !System.Net.IPAddress.IsLoopback(remoteIp))
        {
            return remoteIp.ToString();
        }

        // 2. Conexão direta da Cloudflare: usa o header oficial CF-Connecting-IP.
        if (IsCloudflareIp(remoteIp))
        {
            var cfIp = HttpContext.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(cfIp) && System.Net.IPAddress.TryParse(cfIp.Trim(), out _))
            {
                return cfIp.Trim();
            }
        }

        // 3. Fallback (desenvolvimento/local sem proxy).
        return remoteIp?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Limite de tamanho por tool result enviado ao LLM (defesa em profundidade:
    /// o agent Go já trunca em 16 KB; aqui garantimos o teto também server-side).
    /// </summary>
    private const int MaxToolResultLength = 32 * 1024;

    /// <summary>
    /// Teto específico para capturas de tela: o resultado carrega a imagem em
    /// base64 (contrato "image_base64") e precisa sobreviver INTACTO para a
    /// visão do LLM. Limitado a ~6 MB de base64 por captura (o agent já limita
    /// o lado maior da imagem a 1600 px, então o normal fica bem abaixo).
    /// </summary>
    private const int MaxScreenshotToolResultLength = 8 * 1024 * 1024;

    /// <summary>
    /// Trunca o resultado de uma tool para MaxToolResultLength. Tenta fechar
    /// estruturas JSON abertas; se o resultado truncado não for JSON válido,
    /// devolve texto cru com marcador (nunca JSON quebrado).
    /// </summary>
    internal static string TruncateToolResult(string toolName, string? result)
    {
        var r = result ?? string.Empty;
        // Captura de tela: o teto padrão (32 KB) destruiria a imagem em base64.
        var maxLength = toolName == "capture_screenshot" || r.Contains("\"image_base64\"", StringComparison.Ordinal)
            ? MaxScreenshotToolResultLength
            : MaxToolResultLength;
        if (r.Length <= maxLength) return r;

        var cut = r[..maxLength];
        var trimmed = cut.TrimEnd(' ', '\t', '\r', '\n', ',');

        // Fecha estruturas JSON abertas (contagem de delimitadores fora de strings).
        var stack = new Stack<char>();
        var inStr = false;
        var esc = false;
        foreach (var c in trimmed)
        {
            if (inStr)
            {
                if (esc) { esc = false; }
                else if (c == '\\') { esc = true; }
                else if (c == '"') { inStr = false; }
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; break;
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}' or ']': if (stack.Count > 0) stack.Pop(); break;
            }
        }

        var closed = trimmed + string.Concat(stack);
        if (inStr) closed += "\"";

        // Só usa a versão fechada se for JSON válido; caso contrário, texto cru.
        if (IsValidJson(closed) && !inStr)
        {
            return closed;
        }
        return cut + $"\n...[truncado pelo servidor; tool={toolName}; total={r.Length} chars]";
    }

    /// <summary>
    /// Validação leve de JSON usando System.Text.Json.
    /// </summary>
    private static bool IsValidJson(string s)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(s);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static async Task WriteSseJsonAsync(HttpContext httpContext, object value, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value, SseJsonOptions);
        await httpContext.Response.WriteAsync($"data: {json}\n\n", ct);
    }

    [HttpGet("me/ai-chat/jobs/{jobId}")]
    public async Task<IActionResult> GetAiChatJob(Guid jobId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAiChatJobQuery(id, jobId), ct), Ok);
    }
}