using System.Text.Json;
using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Configurations.Queries;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Cqrs.Support.Csat;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class TicketsController(
    IMediator mediator,
    ISlaService slaService,
    ICustomFieldService customFieldService,
    ITicketQueryService queryService,
    IAttachmentService attachmentService,
    IAttachmentRepository attachmentRepository,
    ITicketRepository ticketRepository,
    IScopeContext scopeContext) : ControllerBase
{
    private string Username => HttpContext.Items["Username"] as string ?? "api";
    private Guid? CurrentUserId => HttpContext.Items["UserId"] as Guid?;

    /// <summary>
    /// Valida o escopo (cliente/site) do ticket para o usuário atual. Usuários com
    /// acesso global passam direto; os demais só acessam tickets no seu ACL.
    /// </summary>
    /// <summary>Atalho para checagens de leitura (escopo View).</summary>
    private Task<bool> CanAccessTicketAsync(Guid ticketId, CancellationToken ct)
        => CanAccessTicketAsync(ticketId, ActionType.View, ct);

    private async Task<bool> CanAccessTicketAsync(
        Guid ticketId,
        ActionType action = ActionType.View,
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, action);
        if (access.HasGlobalAccess) return true;

        var ticket = await queryService.GetTicketByIdAsync(ticketId, ct);
        if (ticket is null) return false;

        return access.AllowedClientIds.Contains(ticket.ClientId)
            || (ticket.SiteId.HasValue && access.AllowedSiteIds.Contains(ticket.SiteId.Value));
    }

    // ── Listagem com paginação cursor ─────────────────────────────────

    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll([FromQuery] TicketFilterQuery filter)
    {
        var result = await mediator.Send(new ListTicketsQuery(filter), HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (!await CanAccessTicketAsync(id, HttpContext.RequestAborted))
            return NotFound();

        var result = await mediator.Send(new GetTicketByIdQuery(id), HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateTicketCommand command)
    {
        // Escopo no create: usuário restrito só cria para cliente/site permitidos.
        var createAccess = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Create);
        if (!createAccess.HasGlobalAccess
            && !createAccess.AllowedClientIds.Contains(command.ClientId)
            && !(command.SiteId.HasValue && createAccess.AllowedSiteIds.Contains(command.SiteId.Value)))
        {
            return NotFound();
        }

        var result = await mediator.Send(command, HttpContext.RequestAborted);
        return result.ToCreatedAtActionResult(nameof(GetById), new { id = result.Value!.Id }, this);
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketCommand command)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(command with { Id = id }, HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    [HttpPatch("{id:guid}/workflow-state")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> UpdateWorkflowState(Guid id, [FromBody] TransitionTicketStateCommand command)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(command with { TicketId = id }, HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}/comments")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetComments(Guid id, [FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        if (!await CanAccessTicketAsync(id, HttpContext.RequestAborted))
            return NotFound();

        // Notas internas: só quem pode editar ESTE chamado vê. O cálculo anterior
        // usava o conjunto global de acesso de edição — um usuário com Edit no
        // cliente A enxergava notas internas de chamados do cliente B (onde só
        // tinha View).
        var includeInternal = await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted);

        var result = await mediator.Send(new GetTicketCommentsQuery(id, cursor, Math.Clamp(limit, 1, 200), includeInternal), HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/comments")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> AddComment(Guid id, [FromBody] AddTicketCommentCommand command)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();

        // Autoria sempre vem do token autenticado — nunca do payload do cliente.
        var result = await mediator.Send(command with { TicketId = id, UserId = CurrentUserId, UserName = Username }, HttpContext.RequestAborted);
        return result.Match<IActionResult>(success: r => CreatedAtAction(nameof(GetComments), new { id }, r), failure: NotFound);
    }

    [HttpPost("{id:guid}/merge")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> MergeTickets(Guid id, [FromBody] MergeTicketsCommand command)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();
        var sourceTicketIds = command.SourceTicketIds;
        if (sourceTicketIds is not null)
        {
            foreach (var sourceTicketId in sourceTicketIds)
            {
                if (!await CanAccessTicketAsync(sourceTicketId, HttpContext.RequestAborted))
                    return NotFound();
            }
        }
        var result = await mediator.Send(
            command with { TargetTicketId = id, ChangedByUserId = CurrentUserId },
            HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}/sla")]
    [HttpGet("{id:guid}/sla/status")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetSlaStatus(Guid id)
    {
        if (!await CanAccessTicketAsync(id, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(new GetTicketSlaStatusQuery(id), HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    // ── By Client ───────────────────────────────────────────────────────

    [HttpGet("by-client/{clientId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetByClient(Guid clientId, [FromQuery] int limit = 100)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (!access.HasGlobalAccess && !access.AllowedClientIds.Contains(clientId))
            return NotFound();

        var filter = new TicketFilterQuery(
            ClientId: clientId,
            Limit: Math.Clamp(limit, 1, 500),
            HasGlobalAccess: access.HasGlobalAccess,
            AllowedClientIds: access.AllowedClientIds,
            AllowedSiteIds: access.AllowedSiteIds);
        var page = await queryService.ListTicketsAsync(filter, HttpContext.RequestAborted);
        return Ok(page);
    }

    // ── Watchers ─────────────────────────────────────────────────────────

    [HttpGet("{id:guid}/watchers")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetWatchers(Guid id)
    {
        if (!await CanAccessTicketAsync(id, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(new GetTicketWatchersQuery(id));
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/watchers")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> AddWatcher(Guid id, [FromBody] AddWatcherRequest request)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(new AddTicketWatcherCommand(id, request.UserId, Username));
        return result.Match<IActionResult>(success: w => CreatedAtAction(nameof(GetWatchers), new { id }, w), failure: BadRequest);
    }

    [HttpDelete("{id:guid}/watchers/{userId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> RemoveWatcher(Guid id, Guid userId)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();
        await mediator.Send(new RemoveTicketWatcherCommand(id, userId));
        return NoContent();
    }

    // ── Remote Sessions ──────────────────────────────────────────────────

    [HttpGet("{id:guid}/remote-sessions")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetRemoteSessions(Guid id)
    {
        if (!await CanAccessTicketAsync(id, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(new GetTicketRemoteSessionsQuery(id));
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/remote-sessions")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> CreateRemoteSession(Guid id, [FromBody] TicketRemoteSession body)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(new CreateTicketRemoteSessionCommand(id, body.AgentId, body.MeshNodeId, Username, body.Note));
        return result.Match<IActionResult>(success: s => CreatedAtAction(nameof(GetRemoteSessions), new { id }, s), failure: BadRequest);
    }

    [HttpPatch("{id:guid}/remote-sessions/{sessionId:guid}/end")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> EndRemoteSession(Guid id, Guid sessionId)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(new EndTicketRemoteSessionCommand(id, sessionId));
        return result.ToActionResult();
    }

    // ── Automation Links ─────────────────────────────────────────────────

    [HttpGet("{id:guid}/automation-links")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAutomationLinks(Guid id)
    {
        if (!await CanAccessTicketAsync(id, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(new GetTicketAutomationLinksQuery(id));
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/automation-links")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> CreateAutomationLink(Guid id, [FromBody] TicketAutomationLink body)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(new CreateTicketAutomationLinkCommand(id, body.AutomationTaskDefinitionId, Username, body.Note));
        return result.Match<IActionResult>(success: l => CreatedAtAction(nameof(GetAutomationLinks), new { id }, l), failure: BadRequest);
    }

    // ── Knowledge Links ──────────────────────────────────────────────────

    [HttpGet("{id:guid}/knowledge-links")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetKnowledgeLinks(Guid id)
    {
        if (!await CanAccessTicketAsync(id, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(new GetTicketKnowledgeLinksQuery(id));
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/knowledge-links")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> CreateKnowledgeLink(Guid id, [FromBody] TicketKnowledgeLink body)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(new CreateTicketKnowledgeLinkCommand(id, body.ArticleId, null, body.Note));
        return result.Match<IActionResult>(success: l => CreatedAtAction(nameof(GetKnowledgeLinks), new { id }, l), failure: BadRequest);
    }

    [HttpDelete("{id:guid}/knowledge-links/{linkId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> DeleteKnowledgeLink(Guid id, Guid linkId)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();
        await mediator.Send(new DeleteTicketKnowledgeLinkCommand(linkId));
        return NoContent();
    }

    /// <summary>
    /// Sugestões de artigos da KB para o ticket (busca híbrida; sem q usa título+descrição).
    /// Endpoint consumido pelo console web — antes não existia (404).
    /// </summary>
    [HttpGet("{id:guid}/knowledge/suggest")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> SuggestKnowledge(Guid id, [FromQuery] string? q = null, [FromQuery] Guid? clientId = null, [FromQuery] Guid? siteId = null, [FromQuery] Guid? departmentId = null, [FromQuery] int maxResults = 5, CancellationToken ct = default)
    {
        if (!await CanAccessTicketAsync(id, ct))
            return NotFound();
        var result = await mediator.Send(new SuggestTicketKnowledgeQuery(id, q, clientId, siteId, departmentId, maxResults), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Feedback (útil/não útil) em um vínculo ticket↔artigo. Completa a migration
    /// M103 — o front chamava este endpoint sem ele existir (404).
    /// </summary>
    [HttpPost("{id:guid}/knowledge/{articleId:guid}/feedback")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> SetKnowledgeLinkFeedback(Guid id, Guid articleId, [FromBody] KbLinkFeedbackRequest body, CancellationToken ct)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, ct))
            return NotFound();
        var result = await mediator.Send(new SetTicketKnowledgeLinkFeedbackCommand(id, articleId, body.Useful), ct);
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── Relations ────────────────────────────────────────────────────────

    [HttpGet("{id:guid}/relations")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetRelations(Guid id)
    {
        if (!await CanAccessTicketAsync(id, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(new GetTicketRelationsQuery(id), HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/relations")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> CreateRelation(Guid id, [FromBody] CreateRelationRequest request)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();
        if (!await CanAccessTicketAsync(request.TargetTicketId, HttpContext.RequestAborted))
            return NotFound();

        var result = await mediator.Send(
            new CreateTicketRelationCommand(id, request.TargetTicketId, request.RelationType, CurrentUserId, Username),
            HttpContext.RequestAborted);
        return result.Match<IActionResult>(success: Ok, failure: BadRequest);
    }

    [HttpDelete("{id:guid}/relations/{relationId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> DeleteRelation(Guid id, Guid relationId)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(
            new DeleteTicketRelationCommand(id, relationId, CurrentUserId), HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    // ── Lifecycle: reopen / rating (CSAT) ───────────────────────────────

    [HttpPost("{id:guid}/reopen")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Reopen(Guid id, [FromBody] ReopenTicketRequest request)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(
            new ReopenTicketCommand(id, request.Reason, CurrentUserId), HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/rating")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Rate(Guid id, [FromBody] RateTicketRequest request)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(
            new RateTicketCommand(id, request.Rating, request.Feedback, CurrentUserId, Username),
            HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    // ── Attachments ─────────────────────────────────────────────────────

    /// <summary>
    /// Lista anexos de um ticket com paginação por cursor.
    /// </summary>
    [HttpGet("{id:guid}/attachments")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAttachments(
        Guid id,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50)
    {
        if (!await CanAccessTicketAsync(id, HttpContext.RequestAborted))
            return NotFound();

        var result = await mediator.Send(new GetTicketAttachmentsQuery(id, cursor, limit));
        return result.ToActionResult();
    }

    /// <summary>
    /// Baixa um anexo do ticket. Valida que o anexo pertence a ESTE ticket e
    /// redireciona para a URL pré-assinada do object storage.
    /// </summary>
    [HttpGet("{id:guid}/attachments/{attachmentId:guid}/download")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> DownloadAttachment(Guid id, Guid attachmentId, CancellationToken ct)
    {
        if (!await CanAccessTicketAsync(id, ct))
            return NotFound();

        var attachment = await attachmentRepository.GetByIdAsync(attachmentId, ct);
        if (attachment is null
            || attachment.IsDeleted
            || !string.Equals(attachment.EntityType, "Ticket", StringComparison.OrdinalIgnoreCase)
            || attachment.EntityId != id)
        {
            return NotFound();
        }

        try
        {
            // Streaming same-origin: um <a href> não envia Authorization e o
            // redirect para o storage exigiria CORS. Aqui a API autentica e serve.
            var stream = await attachmentService.DownloadAttachmentAsync(attachmentId, ct);
            var contentType = string.IsNullOrWhiteSpace(attachment.ContentType)
                ? "application/octet-stream"
                : attachment.ContentType;
            return File(stream, contentType, attachment.FileName);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível baixar o arquivo." });
        }
    }

    /// <summary>
    /// Prepara upload direto no object storage (URL pré-assinada). Respeita a
    /// configuração global de anexos de tickets (habilitação, tipos e tamanho).
    /// </summary>
    [HttpPost("{id:guid}/attachments/presigned-upload")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> PrepareAttachmentUpload(Guid id, [FromBody] PresignedUploadRequestDto req)
    {
        if (!await CanAccessTicketAsync(id, HttpContext.RequestAborted))
            return NotFound();

        var ticket = await queryService.GetTicketByIdAsync(id, HttpContext.RequestAborted);
        if (ticket is null)
            return NotFound();

        var settingsResult = await mediator.Send(new GetTicketAttachmentSettingsQuery(), HttpContext.RequestAborted);
        if (settingsResult.IsFailure || settingsResult.Value is null)
            return BadRequest(new { error = "Não foi possível ler a configuração de anexos." });
        var settings = settingsResult.Value;

        if (!settings.Enabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Anexos de tickets estão desabilitados nas configurações do servidor." });

        if (string.IsNullOrWhiteSpace(req.FileName) || string.IsNullOrWhiteSpace(req.ContentType))
            return BadRequest(new { error = "Nome e tipo do arquivo são obrigatórios." });

        if (!settings.IsContentTypeAllowed(req.ContentType))
            return BadRequest(new { error = $"Tipo de arquivo não permitido: {req.ContentType}." });

        if (req.SizeBytes <= 0 || req.SizeBytes > settings.MaxFileSizeBytes)
            return BadRequest(new { error = $"Tamanho de arquivo inválido. Máximo permitido: {settings.MaxFileSizeBytes} bytes." });

        var result = await attachmentService.PreparePresignedUploadAsync(
            "Ticket", id, ticket.ClientId, req.FileName, req.ContentType, req.SizeBytes,
            settings.PresignedUploadUrlTtlMinutes, HttpContext.RequestAborted);

        return Ok(result);
    }

    /// <summary>
    /// Finaliza o upload pré-assinado e persiste o anexo do ticket.
    /// </summary>
    [HttpPost("{id:guid}/attachments/complete-upload")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> CompleteAttachmentUpload(Guid id, [FromBody] CompleteUploadRequestDto req)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();

        var ticket = await queryService.GetTicketByIdAsync(id, HttpContext.RequestAborted);
        if (ticket is null)
            return NotFound();

        var settingsResult = await mediator.Send(new GetTicketAttachmentSettingsQuery(), HttpContext.RequestAborted);
        if (settingsResult.IsFailure || settingsResult.Value is null)
            return BadRequest(new { error = "Não foi possível ler a configuração de anexos." });
        var settings = settingsResult.Value;

        if (!settings.Enabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Anexos de tickets estão desabilitados nas configurações do servidor." });

        if (!settings.IsContentTypeAllowed(req.ContentType))
            return BadRequest(new { error = $"Tipo de arquivo não permitido: {req.ContentType}." });

        if (req.SizeBytes <= 0 || req.SizeBytes > settings.MaxFileSizeBytes)
            return BadRequest(new { error = $"Tamanho de arquivo inválido. Máximo permitido: {settings.MaxFileSizeBytes} bytes." });

        try
        {
            // Autor é sempre o usuário autenticado — UploadedBy do body é ignorado.
            var attachment = await attachmentService.CompletePresignedUploadAsync(
                req.AttachmentId, "Ticket", id, ticket.ClientId,
                req.FileName, req.ContentType, req.SizeBytes, req.ObjectKey,
                Username, HttpContext.RequestAborted);

            // O limite vale sobre o tamanho REAL no storage (não o declarado).
            if (attachment.SizeBytes > settings.MaxFileSizeBytes)
            {
                await attachmentService.DeleteAttachmentAsync(attachment.Id, HttpContext.RequestAborted);
                return StatusCode(StatusCodes.Status413RequestEntityTooLarge, new { error = $"Arquivo excede o tamanho máximo permitido ({settings.MaxFileSizeBytes} bytes)." });
            }

            return Ok(attachment);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── Audit Timeline ───────────────────────────────────────────────────

    [HttpGet("{id:guid}/audit/timeline")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAuditTimeline(Guid id)
    {
        if (!await CanAccessTicketAsync(id, HttpContext.RequestAborted))
            return NotFound();
        var result = await mediator.Send(new GetTicketAuditTimelineQuery(id));
        return result.ToActionResult();
    }

    // ── SLA Details ──────────────────────────────────────────────────────

    [HttpGet("{id:guid}/sla/details")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetSlaDetails(Guid id)
    {
        if (!await CanAccessTicketAsync(id, HttpContext.RequestAborted))
            return NotFound();
        var (slaHours, slaPercent, slaBreached) = await slaService.GetSlaStatusAsync(id);
        var (frtHours, frtPercent, frtBreached, frtAchieved) = await slaService.GetFrtStatusAsync(id);

        var ticket = await ticketRepository.GetByIdAsync(id);
        var onHold = ticket?.SlaHoldStartedAt.HasValue == true;
        var warningLevel = slaPercent >= 90 ? "critical" : slaPercent >= 75 ? "high" : slaPercent >= 50 ? "medium" : "low";

        // Contrato plano consumido pelo console (tipo SlaDetails): antes o endpoint
        // devolvia só { resolution, firstResponse }, então o painel de SLA do
        // detalhe do chamado nunca exibia percentual/expiração (campos undefined).
        return Ok(new
        {
            ticketId = id,
            slaExpiresAt = ticket?.SlaExpiresAt,
            effectiveSlaExpiresAt = ticket is null ? null : slaService.GetEffectiveSlaExpiry(ticket),
            hoursRemaining = (double)slaHours,
            percentUsed = slaPercent,
            breached = slaBreached,
            status = slaBreached ? "SLA violado" : onHold ? "SLA em pausa" : "SLA ativo",
            message = ticket?.SlaExpiresAt is null ? "SLA não configurado para este chamado." : null,
            onHold,
            slaHoldStartedAt = ticket?.SlaHoldStartedAt,
            slaPausedSeconds = ticket?.SlaPausedSeconds ?? 0,
            warningLevel,
            firstResponseSla = new
            {
                slaFirstResponseExpiresAt = ticket?.SlaFirstResponseExpiresAt,
                firstRespondedAt = ticket?.FirstRespondedAt,
                hoursRemaining = (double)frtHours,
                percentUsed = frtPercent,
                breached = frtBreached,
                achieved = frtAchieved,
            },
            // Compatibilidade com consumidores do formato antigo.
            resolution = new { hoursRemaining = slaHours, percentUsed = slaPercent, breached = slaBreached },
            firstResponse = new { hoursRemaining = frtHours, percentUsed = frtPercent, breached = frtBreached, achieved = frtAchieved },
        });
    }

    // ── Custom Fields ────────────────────────────────────────────────────

    [HttpGet("{id:guid}/custom-fields")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetCustomFields(Guid id, [FromQuery] bool includeSecrets = false)
    {
        if (!await CanAccessTicketAsync(id, HttpContext.RequestAborted))
            return NotFound();
        return Ok(await customFieldService.GetValuesAsync(CustomFieldScopeType.Ticket, id, includeSecrets, HttpContext.RequestAborted));
    }

    [HttpPut("{id:guid}/custom-fields/{definitionId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> UpsertCustomField(Guid id, Guid definitionId, [FromBody] JsonElement body)
    {
        if (!await CanAccessTicketAsync(id, ActionType.Edit, HttpContext.RequestAborted))
            return NotFound();

        // Aceita tanto o formato { "value": ... } quanto o valor cru (string, número, etc.)
        // enviado diretamente como corpo JSON. TryGetProperty só é seguro em objetos.
        var valueJson = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("value", out var prop)
            ? prop.GetRawText()
            : body.GetRawText();
        var result = await customFieldService.UpsertValueAsync(new UpsertCustomFieldValueInput(definitionId, CustomFieldScopeType.Ticket, id, valueJson, Username), HttpContext.RequestAborted);
        return Ok(result);
    }

    // ── CSAT ─────────────────────────────────────────────────────────────

    /// <summary>Resumo de satisfação (CSAT) por período, cliente e departamento.</summary>
    [HttpGet("csat/summary")]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetCsatSummary(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? departmentId = null)
        => (await mediator.Send(new GetTicketCsatSummaryQuery(from, to, clientId, departmentId), HttpContext.RequestAborted)).ToActionResult();

    // ── KPI ──────────────────────────────────────────────────────────────

    [HttpGet("kpi")]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetKpi([FromQuery] TicketFilterQuery filter)
    {
        var result = await mediator.Send(new GetTicketKpiQuery(filter));
        return result.ToActionResult();
    }

    private IActionResult BadRequest(IReadOnlyList<Discovery.Core.Cqrs.Error> errors) => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) });
    private IActionResult NotFound(IReadOnlyList<Discovery.Core.Cqrs.Error> errors) => errors[0].Code == "NotFound" ? NotFound() : BadRequest(errors);
}

public record AddWatcherRequest(Guid UserId);

public record CreateRelationRequest(Guid TargetTicketId, TicketRelationType RelationType);

public record ReopenTicketRequest(string? Reason);

public record RateTicketRequest(int Rating, string? Feedback);

public record PresignedUploadRequestDto(string FileName, string ContentType, long SizeBytes);

public record CompleteUploadRequestDto(
    Guid AttachmentId,
    string ObjectKey,
    string FileName,
    string ContentType,
    long SizeBytes,
    string? UploadedBy = null);
