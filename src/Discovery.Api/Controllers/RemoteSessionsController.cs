using Discovery.Api.Filters;
using Discovery.Core.Cqrs.RemoteSessions.Commands;
using Discovery.Core.Cqrs.RemoteSessions.Queries;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoints de sessões de acesso remoto nativo (screen, terminal, files, proxy).
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class RemoteSessionsController : ControllerBase
{
    private readonly IMediator _mediator;

    public RemoteSessionsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>Inicia uma nova sessão de acesso remoto.</summary>
    [HttpPost("{agentId:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> StartSession(Guid agentId, [FromBody] StartRemoteSessionCommand cmd, CancellationToken ct = default)
    {
        var result = await _mediator.Send(cmd with { AgentId = agentId }, ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Encerra uma sessão remota ativa.</summary>
    [HttpPost("{agentId:guid}/{sessionId:guid}/stop")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> StopSession(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new StopRemoteSessionCommand(agentId, sessionId, userId), ct);
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Renova o TTL de uma sessão remota.</summary>
    [HttpPost("{agentId:guid}/{sessionId:guid}/renew")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> RenewSession(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new RenewRemoteSessionCommand(agentId, sessionId, userId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Lista sessões ativas para um agent.</summary>
    [HttpGet("{agentId:guid}/active")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetActiveSessions(Guid agentId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new GetActiveSessionsQuery(agentId, userId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Obtém credenciais TURN para WebRTC.</summary>
    [HttpPost("{agentId:guid}/{sessionId:guid}/turn-credentials")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> GetTurnCredentials(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new GetTurnCredentialsQuery(agentId, sessionId, userId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    private Guid GetUserId()
    {
        if (HttpContext.Items["UserId"] is Guid uid) return uid;
        if (HttpContext.Items["UserId"] is string uidStr && Guid.TryParse(uidStr, out var parsed)) return parsed;
        return Guid.Empty;
    }
}
