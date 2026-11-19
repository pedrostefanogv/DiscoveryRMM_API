using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Agents.Notifications.Commands;
using Discovery.Core.Cqrs.Alerts.Commands;
using Discovery.Core.Cqrs.Alerts.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-alerts")]
public class AgentAlertsController(
    IMediator mediator,
    IScopeContext scopeContext,
    ISiteRepository siteRepository) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll(
        [FromServices] IAgentRepository agentRepository,
        [FromServices] DiscoveryDbContext db,
        [FromQuery] string? status, [FromQuery] string? scopeType,
        [FromQuery] Guid? scopeClientId, [FromQuery] Guid? scopeSiteId,
        [FromQuery] Guid? scopeAgentId, [FromQuery] Guid? ticketId,
        [FromQuery] string? cursor, [FromQuery] int limit = 100)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.View);
        if (!access.HasGlobalAccess)
        {
            if (scopeAgentId.HasValue)
            {
                if (await EnsureAgentScopeAsync(scopeAgentId.Value, access, agentRepository) is { } denied)
                    return denied;
            }
            else if (scopeSiteId.HasValue)
            {
                if (await EnsureSiteScopeAsync(scopeSiteId.Value, access) is { } denied)
                    return denied;
            }
            else if (scopeClientId.HasValue)
            {
                if (!access.AllowedClientIds.Contains(scopeClientId.Value))
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });
            }
            else
            {
                var clampedLimit = Math.Clamp(limit, 1, 200);
                var allowedClients = access.AllowedClientIds;
                var allowedSites = access.AllowedSiteIds;
                var query = db.AgentAlertDefinitions.AsNoTracking()
                    .Where(a =>
                        (a.ScopeClientId != null && allowedClients.Contains(a.ScopeClientId.Value)) ||
                        (a.ScopeSiteId != null && (allowedSites.Contains(a.ScopeSiteId.Value) ||
                            db.Sites.Any(s => s.Id == a.ScopeSiteId.Value && allowedClients.Contains(s.ClientId)))) ||
                        (a.ScopeAgentId != null && db.Agents.Any(ag => ag.Id == a.ScopeAgentId.Value &&
                            (allowedSites.Contains(ag.SiteId) ||
                             db.Sites.Any(s => s.Id == ag.SiteId && allowedClients.Contains(s.ClientId))))));

                if (status is not null && Enum.TryParse<AlertDefinitionStatus>(status, true, out var parsedStatus))
                    query = query.Where(a => a.Status == parsedStatus);
                if (scopeType is not null && Enum.TryParse<AlertScopeType>(scopeType, true, out var parsedScopeType))
                    query = query.Where(a => a.ScopeType == parsedScopeType);
                if (ticketId.HasValue)
                    query = query.Where(a => a.TicketId == ticketId.Value);

                var rows = await query
                    .OrderByDescending(a => a.CreatedAt)
                    .ThenByDescending(a => a.Id)
                    .Take(clampedLimit + 1)
                    .ToListAsync();

                var hasMore = rows.Count > clampedLimit;
                var page = hasMore ? rows.Take(clampedLimit).ToList() : rows;
                var nextCursor = hasMore && page.Count > 0
                    ? CursorPaginationHelper.EncodeCreatedAtCursor(page[^1].CreatedAt, page[^1].Id)
                    : null;
                var dtos = page.Select(a => new AlertDto(a.Id, a.ScopeAgentId, a.Title, a.AlertType.ToString(), a.Status.ToString(), a.CreatedAt, a.TicketId)).ToList();
                return Ok(new ListAlertsResult(dtos, nextCursor, hasMore, dtos.Count));
            }
        }

        var q = new ListAgentAlertsQuery(status, scopeType, scopeClientId, scopeSiteId, scopeAgentId, ticketId, cursor, limit);
        var r = await mediator.Send(q);
        return r.Match<IActionResult>(Ok, e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetById(
        Guid id,
        [FromServices] IAgentAlertRepository alertRepo,
        [FromServices] IAgentRepository agentRepo)
    {
        if (await EnsureAlertAccessAsync(id, ResourceType.Agents, ActionType.View, alertRepo, agentRepo) is { } denied)
            return denied;

        var r = await mediator.Send(new GetAlertByIdQuery(id));
        return r.Match<IActionResult>(Ok, e => e[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpPost]
    [RequirePermission(ResourceType.Agents, ActionType.Create, ScopeSource.AccessList)]
    public async Task<IActionResult> Create(
        [FromBody] CreateAlertCommand cmd,
        [FromServices] IAgentRepository agentRepo)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.Create);
        if (!access.HasGlobalAccess)
        {
            if (cmd.AgentId.HasValue)
            {
                if (await EnsureAgentScopeAsync(cmd.AgentId.Value, access, agentRepo) is { } denied)
                    return denied;
            }
            else if (cmd.SiteId.HasValue)
            {
                if (await EnsureSiteScopeAsync(cmd.SiteId.Value, access) is { } denied)
                    return denied;
            }
            else if (cmd.ClientId.HasValue)
            {
                if (!access.AllowedClientIds.Contains(cmd.ClientId.Value))
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });
            }
            else
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Escopo insuficiente para criar alerta sem destino." });
            }
        }

        var r = await mediator.Send(cmd);
        return r.Match<IActionResult>(dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto), e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpPost("{id:guid}/dispatch")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> Dispatch(
        Guid id,
        [FromServices] IAgentAlertRepository alertRepo,
        [FromServices] IAgentRepository agentRepo)
    {
        if (await EnsureAlertAccessAsync(id, ResourceType.Agents, ActionType.Execute, alertRepo, agentRepo) is { } denied)
            return denied;

        var r = await mediator.Send(new DispatchAlertCommand(id));
        return r.Match<IActionResult>(_ => Ok(new { dispatched = true }), e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpPost("{id:guid}/create-ticket")]
    [RequirePermission(ResourceType.Tickets, ActionType.Create, ScopeSource.AccessList)]
    public async Task<IActionResult> CreateTicket(
        Guid id,
        [FromBody] CreateTicketFromAlertCommand cmd,
        [FromServices] IAgentAlertRepository alertRepo,
        [FromServices] IAgentRepository agentRepo)
    {
        if (await EnsureAlertAccessAsync(id, ResourceType.Tickets, ActionType.Create, alertRepo, agentRepo) is { } denied)
            return denied;

        var r = await mediator.Send(cmd with { AlertId = id });
        return r.Match<IActionResult>(Ok, e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Delete, ScopeSource.AccessList)]
    public async Task<IActionResult> Cancel(
        Guid id,
        [FromServices] IAgentAlertRepository alertRepo,
        [FromServices] IAgentRepository agentRepo)
    {
        if (await EnsureAlertAccessAsync(id, ResourceType.Agents, ActionType.Delete, alertRepo, agentRepo) is { } denied)
            return denied;

        var r = await mediator.Send(new CancelAlertCommand(id));
        return r.Match<IActionResult>(_ => NoContent(), e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    /// <summary>
    /// Envia uma notificação avulsa (prompt modal PSADT ou toast) para a sessão
    /// interativa do usuário de um agent. Usada pelo menu de contexto do agent
    /// e pela página de detalhes ("Enviar notificação").
    /// </summary>
    [HttpPost("notify")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> Notify(
        [FromBody] SendAgentNotificationRequest request,
        [FromServices] IAgentRepository agentRepo,
        CancellationToken ct = default)
    {
        if (request is null)
            return BadRequest(new { error = "corpo da requisição é obrigatório." });

        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.Execute);
        if (!access.HasGlobalAccess)
        {
            if (await EnsureAgentScopeAsync(request.AgentId, access, agentRepo) is { } denied)
                return denied;
        }

        var cmd = new SendAgentNotificationCommand(
            request.AgentId,
            request.Title ?? string.Empty,
            request.Message ?? string.Empty,
            request.AlertType,
            request.TimeoutSeconds,
            request.Icon,
            request.DefaultAction);

        var r = await mediator.Send(cmd, ct);
        return r.Match<IActionResult>(
            alertId => Ok(new { success = true, dispatched = true, alertId, agentId = request.AgentId }),
            errors =>
            {
                var error = errors[0];
                return error.Code == "NotFound"
                    ? NotFound(new { error = error.Message })
                    : BadRequest(new { error = error.Message });
            });
    }

    /// <summary>
    /// Envia a mesma notificação (prompt modal PSADT ou toast) para todos os
    /// agents de um escopo: cliente, site, label ou um único agent. Usada pelos
    /// botões "Notificar" nas telas de cliente e de site.
    /// </summary>
    [HttpPost("notify/broadcast")]
    // AccessList (e não Global): usuários com Agents.Execute limitado a
    // cliente/site precisam conseguir notificar o próprio escopo. O escopo do
    // corpo é validado em CanAccessScopeAsync antes do despacho.
    [RequirePermission(ResourceType.Agents, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> NotifyBroadcast(
        [FromBody] SendScopeNotificationRequest request,
        CancellationToken ct = default)
    {
        if (request is null)
            return BadRequest(new { error = "corpo da requisição é obrigatório." });

        // Row-level security: um usuário restrito não pode fazer broadcast para
        // clientes/sites fora do seu escopo, mesmo conhecendo os GUIDs.
        if (!await CanAccessScopeAsync(request))
            return NotFound(new { error = "Escopo de notificação não encontrado." });

        var cmd = new SendScopeNotificationCommand(
            request.ScopeType,
            request.Title ?? string.Empty,
            request.Message ?? string.Empty,
            request.ScopeClientId,
            request.ScopeSiteId,
            request.ScopeAgentId,
            request.ScopeLabelName,
            request.AlertType,
            request.TimeoutSeconds,
            request.Icon);

        var r = await mediator.Send(cmd, ct);
        return r.Match<IActionResult>(
            result => Ok(new
            {
                success = true,
                dispatched = result.Dispatched > 0,
                alertId = result.AlertId,
                scopeType = result.ScopeType,
                totalAgents = result.TotalAgents,
                dispatchedCount = result.Dispatched,
                failedCount = result.Failed
            }),
            errors =>
            {
                var error = errors[0];
                return error.Code == "NotFound"
                    ? NotFound(new { error = error.Message })
                    : BadRequest(new { error = error.Message });
            });
    }

    /// <summary>
    /// Confere se o usuário autenticado pode notificar o escopo solicitado.
    /// Global passa direto; cliente/site exigem que o id esteja na lista de
    /// acesso do usuário (um site é permitido também quando o cliente dele está
    /// liberado).
    /// </summary>
    private async Task<bool> CanAccessScopeAsync(SendScopeNotificationRequest request)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.Execute);
        if (access.HasGlobalAccess)
            return true;

        switch (request.ScopeType)
        {
            case AlertScopeType.Client:
                return request.ScopeClientId is { } clientId
                    && access.AllowedClientIds.Contains(clientId);

            case AlertScopeType.Site:
                if (request.ScopeSiteId is not { } siteId)
                    return false;
                if (access.AllowedSiteIds.Contains(siteId))
                    return true;

                var site = await siteRepository.GetByIdAsync(siteId);
                return site is not null && access.AllowedClientIds.Contains(site.ClientId);

            default:
                // Agent/Label dependem de checagem por agente; não expostos aqui.
                return false;
        }
    }

    private async Task<IActionResult?> EnsureAlertAccessAsync(
        Guid alertId,
        ResourceType resource,
        ActionType action,
        IAgentAlertRepository alertRepo,
        IAgentRepository agentRepo)
    {
        var alert = await alertRepo.GetByIdAsync(alertId);
        if (alert is null)
            return NotFound(new { error = $"Alert {alertId} not found" });

        var access = await scopeContext.GetAccessAsync(resource, action);
        if (access.HasGlobalAccess)
            return null;

        if (alert.ScopeAgentId.HasValue)
            return await EnsureAgentScopeAsync(alert.ScopeAgentId.Value, access, agentRepo);

        if (alert.ScopeSiteId.HasValue)
            return await EnsureSiteScopeAsync(alert.ScopeSiteId.Value, access);

        if (alert.ScopeClientId.HasValue)
        {
            return access.AllowedClientIds.Contains(alert.ScopeClientId.Value)
                ? null
                : StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este alerta." });
        }

        return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este alerta." });
    }

    private async Task<IActionResult?> EnsureAgentScopeAsync(
        Guid agentId,
        UserScopeAccess access,
        IAgentRepository agentRepo)
    {
        var agent = await agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound(new { error = "Agent not found." });

        var site = await siteRepository.GetByIdAsync(agent.SiteId);
        if (!TenantScopePolicy.Allows(access, site?.ClientId ?? Guid.Empty, agent.SiteId))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este agente." });

        return null;
    }

    private async Task<IActionResult?> EnsureSiteScopeAsync(
        Guid siteId,
        UserScopeAccess access)
    {
        if (access.AllowedSiteIds.Contains(siteId))
            return null;

        var site = await siteRepository.GetByIdAsync(siteId);
        if (site is null)
            return NotFound(new { error = "Site not found." });

        if (!TenantScopePolicy.Allows(access, site.ClientId, siteId))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este site." });

        return null;
    }
}

/// <summary>Payload de envio de notificação avulsa para um agent.</summary>
public sealed record SendAgentNotificationRequest(
    Guid AgentId,
    string? Title,
    string? Message,
    PsadtAlertType AlertType = PsadtAlertType.Modal,
    int? TimeoutSeconds = null,
    string? Icon = null,
    string? DefaultAction = null);

/// <summary>Payload de broadcast de notificação para um escopo.</summary>
public sealed record SendScopeNotificationRequest(
    AlertScopeType ScopeType,
    string? Title,
    string? Message,
    Guid? ScopeClientId = null,
    Guid? ScopeSiteId = null,
    Guid? ScopeAgentId = null,
    string? ScopeLabelName = null,
    PsadtAlertType AlertType = PsadtAlertType.Modal,
    int? TimeoutSeconds = null,
    string? Icon = null);
