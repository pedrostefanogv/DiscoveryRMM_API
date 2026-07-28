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
[Route("api/v{version:apiVersion}/remote-sessions")]
public class RemoteSessionsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<RemoteSessionsController> _logger;

    public RemoteSessionsController(IMediator mediator, ILogger<RemoteSessionsController> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    /// <summary>Inicia uma nova sessão de acesso remoto.</summary>
    [HttpPost("{agentId:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> StartSession(Guid agentId, [FromBody] StartRemoteSessionCommand cmd, CancellationToken ct = default)
    {
        var rawUserId = HttpContext.Items["UserId"];
        if (rawUserId is not Guid userId || userId == Guid.Empty)
        {
            _logger.LogWarning("[RemoteSession] StartSession: UserId ausente ou vazio no HttpContext. Raw={RawUserId}, IsApiToken={IsApiToken}, Path={Path}",
                rawUserId, HttpContext.Items["IsApiTokenAuth"], HttpContext.Request.Path);
            return Unauthorized(new { error = "User not authenticated." });
        }

        var tenantId = HttpContext.Items["TenantId"] as Guid? ?? Guid.Empty;
        var siteId = HttpContext.Items["SiteId"] as Guid? ?? Guid.Empty;

        var result = await _mediator.Send(cmd with { AgentId = agentId, UserId = userId, TenantId = tenantId, SiteId = siteId, Force = true }, ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Encerra uma sessão remota ativa.</summary>
    [HttpPost("{agentId:guid}/{sessionId:guid}/stop")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
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
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
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
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> GetTurnCredentials(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new GetTurnCredentialsQuery(agentId, sessionId, userId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Obtém credenciais NATS (JWT + NKey) para o viewer se conectar ao stream.</summary>
    [HttpPost("{agentId:guid}/{sessionId:guid}/nats-credentials")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> GetSessionCredentials(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new GetSessionCredentialsQuery(agentId, sessionId, userId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Inicia a gravação de uma sessão remota.</summary>
    [HttpPost("{agentId:guid}/{sessionId:guid}/recording/start")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> StartRecording(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new StartRecordingCommand(agentId, sessionId, userId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Para a gravação de uma sessão remota.</summary>
    [HttpPost("{agentId:guid}/{sessionId:guid}/recording/stop")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> StopRecording(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new StopRecordingCommand(agentId, sessionId, userId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Obtém URL de download da gravação de uma sessão.</summary>
    [HttpGet("{agentId:guid}/{sessionId:guid}/recording/download")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.View)]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetRecordingDownload(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new GetRecordingDownloadQuery(agentId, sessionId, userId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Exclui a gravação de uma sessão (LGPD Art. 18).</summary>
    [HttpDelete("{agentId:guid}/{sessionId:guid}/recording")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> DeleteRecording(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        // Exclusão assíncrona via recording service
        // O handler de delete será implementado junto com o storage completo (Fase 8)
        return NoContent();
    }

    private Guid GetUserId()
    {
        if (HttpContext.Items["UserId"] is Guid uid) return uid;
        if (HttpContext.Items["UserId"] is string uidStr && Guid.TryParse(uidStr, out var parsed)) return parsed;
        return Guid.Empty;
    }
}
