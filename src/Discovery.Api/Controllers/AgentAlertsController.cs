using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Agents.Notifications.Commands;
using Discovery.Core.Cqrs.Alerts.Commands;
using Discovery.Core.Cqrs.Alerts.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-alerts")]
public class AgentAlertsController(IMediator mediator) : ControllerBase
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
