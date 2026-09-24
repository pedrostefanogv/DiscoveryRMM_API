using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Agents.Notifications.Commands;
using Discovery.Core.Cqrs.Alerts.Commands;
using Discovery.Core.Cqrs.Alerts.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-alerts")]
public class AgentAlertsController(
    IMediator mediator,
    IScopeContext scopeContext,
    ISiteRepository siteRepository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? status, [FromQuery] string? scopeType,
        [FromQuery] Guid? scopeClientId, [FromQuery] Guid? scopeSiteId,
        [FromQuery] Guid? scopeAgentId, [FromQuery] Guid? ticketId,
        [FromQuery] string? cursor, [FromQuery] int limit = 100)
    {
        var q = new ListAgentAlertsQuery(status, scopeType, scopeClientId, scopeSiteId, scopeAgentId, ticketId, cursor, limit);
        var r = await mediator.Send(q);
        return r.Match<IActionResult>(Ok, e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var r = await mediator.Send(new GetAlertByIdQuery(id));
        return r.Match<IActionResult>(Ok, e => e[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAlertCommand cmd)
    {
        var r = await mediator.Send(cmd);
        return r.Match<IActionResult>(dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto), e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpPost("{id:guid}/dispatch")]
    public async Task<IActionResult> Dispatch(Guid id)
    {
        var r = await mediator.Send(new DispatchAlertCommand(id));
        return r.Match<IActionResult>(_ => Ok(new { dispatched = true }), e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpPost("{id:guid}/create-ticket")]
    public async Task<IActionResult> CreateTicket(Guid id, [FromBody] CreateTicketFromAlertCommand cmd)
    {
        var r = await mediator.Send(cmd with { AlertId = id });
        return r.Match<IActionResult>(Ok, e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var r = await mediator.Send(new CancelAlertCommand(id));
        return r.Match<IActionResult>(_ => NoContent(), e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    /// <summary>
    /// Envia uma notificação avulsa (prompt modal PSADT ou toast) para a sessão
    /// interativa do usuário de um agent. Usada pelo menu de contexto do agent
    /// e pela página de detalhes ("Enviar notificação").
    /// </summary>
    [HttpPost("notify")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> Notify([FromBody] SendAgentNotificationRequest request, CancellationToken ct = default)
    {
        if (request is null)
            return BadRequest(new { error = "corpo da requisição é obrigatório." });

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
