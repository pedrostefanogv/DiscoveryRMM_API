using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent remote debug session endpoints (start/stop).
/// </summary>
public partial class AgentsController
{
    [HttpPost("{id:guid}/remote-debug/start")]
    public async Task<IActionResult> StartRemoteDebug(Guid id, [FromBody] StartRemoteDebugRequest? request)
    {
        if (HttpContext.Items["UserId"] is not Guid userId) return Unauthorized(new { error = "User not authenticated." });
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound(new { error = "Agent not found." });
        var site = await _siteRepository.GetByIdAsync(agent.SiteId);
        if (site is null) return NotFound(new { error = "Site not found." });

        if (!await _permissionService.HasPermissionAsync(userId, ResourceType.RemoteDebug, ActionType.Execute, ScopeLevel.Site, agent.SiteId, site.ClientId))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Acesso negado para remote debug." });

        var session = _remoteDebugSessionManager.StartSession(id, userId, site.ClientId, agent.SiteId, request?.LogLevel, request?.TtlMinutes, request?.PreferredTransport);
        var payload = JsonSerializer.Serialize(new
        {
            action = "start",
            sessionId = session.SessionId,
            logLevel = session.LogLevel,
            expiresAtUtc = session.ExpiresAtUtc,
            stream = new
            {
                natsSubject = session.NatsSubject
            }
        });

        if (!_remoteDebugPayloadValidator.TryNormalize(CommandType.RemoteDebug, payload, out var normalizedPayload, out var validationError))
            return BadRequest(new { error = validationError });

        var command = new AgentCommand { AgentId = id, CommandType = CommandType.RemoteDebug, Payload = normalizedPayload };
        var created = await _commandDispatcher.DispatchAsync(command);

        return Ok(new RemoteDebugStartResponse(session.SessionId, created.Id, session.AgentId, session.LogLevel, session.StartedAtUtc, session.ExpiresAtUtc, session.PreferredTransport, session.NatsSubject));
    }

    [HttpPost("{id:guid}/remote-debug/{sessionId:guid}/stop")]
    public async Task<IActionResult> StopRemoteDebug(Guid id, Guid sessionId)
    {
        if (HttpContext.Items["UserId"] is not Guid userId) return Unauthorized(new { error = "User not authenticated." });
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound(new { error = "Agent not found." });
        var site = await _siteRepository.GetByIdAsync(agent.SiteId);
        if (site is null) return NotFound(new { error = "Site not found." });

        if (!await _permissionService.HasPermissionAsync(userId, ResourceType.RemoteDebug, ActionType.Execute, ScopeLevel.Site, agent.SiteId, site.ClientId))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Acesso negado para remote debug." });

        if (!_remoteDebugSessionManager.TryGetSessionForUser(sessionId, userId, out var session) || session is null)
            return NotFound(new { error = "Remote debug session not found." });
        if (session.AgentId != id) return BadRequest(new { error = "Session does not belong to this agent." });

        var payload = JsonSerializer.Serialize(new
        {
            action = "stop",
            sessionId,
            stream = new
            {
                natsSubject = session.NatsSubject
            }
        });

        if (!_remoteDebugPayloadValidator.TryNormalize(CommandType.RemoteDebug, payload, out var normalizedPayload, out var validationError))
            return BadRequest(new { error = validationError });

        var command = new AgentCommand { AgentId = id, CommandType = CommandType.RemoteDebug, Payload = normalizedPayload };
        var created = await _commandDispatcher.DispatchAsync(command);
        _remoteDebugSessionManager.CloseSession(sessionId, "stopped-by-user", userId);
        return Ok(new { sessionId, commandId = created.Id, stoppedAtUtc = DateTime.UtcNow });
    }
}
