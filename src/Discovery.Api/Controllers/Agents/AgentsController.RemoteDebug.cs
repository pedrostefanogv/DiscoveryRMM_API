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
            return Forbid();

        var session = _remoteDebugSessionManager.StartSession(id, userId, site.ClientId, agent.SiteId, request?.LogLevel, request?.TtlMinutes);
        var payload = JsonSerializer.Serialize(new { action = "start", sessionId = session.SessionId, logLevel = session.LogLevel, startedAtUtc = session.StartedAtUtc, expiresAtUtc = session.ExpiresAtUtc, stream = new { preferredTransport = session.PreferredTransport, fallbackTransport = session.FallbackTransport, natsSubject = session.NatsSubject, signalRMethod = session.SignalRMethod } });
        var command = new AgentCommand { AgentId = id, CommandType = CommandType.RemoteDebug, Payload = payload };
        var created = await _commandDispatcher.DispatchAsync(command);

        return Ok(new RemoteDebugStartResponse(session.SessionId, created.Id, session.AgentId, session.LogLevel, session.StartedAtUtc, session.ExpiresAtUtc, session.PreferredTransport, session.FallbackTransport, session.NatsSubject, session.SignalRMethod));
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
            return Forbid();

        if (!_remoteDebugSessionManager.TryGetSessionForUser(sessionId, userId, out var session) || session is null)
            return NotFound(new { error = "Remote debug session not found." });
        if (session.AgentId != id) return BadRequest(new { error = "Session does not belong to this agent." });

        var payload = JsonSerializer.Serialize(new { action = "stop", sessionId, stoppedAtUtc = DateTime.UtcNow });
        var command = new AgentCommand { AgentId = id, CommandType = CommandType.RemoteDebug, Payload = payload };
        var created = await _commandDispatcher.DispatchAsync(command);
        _remoteDebugSessionManager.CloseSession(sessionId, "stopped-by-user", userId);
        return Ok(new { sessionId, commandId = created.Id, stoppedAtUtc = DateTime.UtcNow });
    }
}
