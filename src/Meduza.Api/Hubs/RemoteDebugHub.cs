using Meduza.Api.Services;
using Meduza.Core.Enums.Identity;
using Meduza.Core.Interfaces.Auth;
using Microsoft.AspNetCore.SignalR;

namespace Meduza.Api.Hubs;

public class RemoteDebugHub : Hub
{
    private readonly IRemoteDebugSessionManager _sessionManager;
    private readonly IPermissionService _permissionService;

    public RemoteDebugHub(IRemoteDebugSessionManager sessionManager, IPermissionService permissionService)
    {
        _sessionManager = sessionManager;
        _permissionService = permissionService;
    }

    public async Task JoinSession(Guid sessionId)
    {
        if (Context.Items["UserId"] is not Guid userId)
            throw new HubException("Not authenticated as user.");

        if (!_sessionManager.TryGetSession(sessionId, out var session) || session is null)
            throw new HubException("Session not found or not authorized.");

        if (session.OwnerUserId != userId)
        {
            var hasAccess = await _permissionService.HasPermissionAsync(
                userId,
                ResourceType.RemoteDebug,
                ActionType.View,
                ScopeLevel.Site,
                session.SiteId,
                session.ClientId);

            if (!hasAccess)
                throw new HubException("Session not found or not authorized.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RemoteDebugGroupNames.ForSession(sessionId));
        _sessionManager.Touch(sessionId);

        await Clients.Caller.SendAsync("RemoteDebugSessionJoined", new
        {
            sessionId = session.SessionId,
            agentId = session.AgentId,
            startedAtUtc = session.StartedAtUtc,
            expiresAtUtc = session.ExpiresAtUtc,
            preferredTransport = session.PreferredTransport
        });
    }

    public Task LeaveSession(Guid sessionId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, RemoteDebugGroupNames.ForSession(sessionId));

    public async Task CloseSession(Guid sessionId, string? reason)
    {
        if (Context.Items["UserId"] is not Guid userId)
            throw new HubException("Not authenticated as user.");

        if (!_sessionManager.TryGetSession(sessionId, out var session) || session is null)
            throw new HubException("Session not found or not authorized.");

        if (session.OwnerUserId != userId)
        {
            var hasAccess = await _permissionService.HasPermissionAsync(
                userId,
                ResourceType.RemoteDebug,
                ActionType.Execute,
                ScopeLevel.Site,
                session.SiteId,
                session.ClientId);

            if (!hasAccess)
                throw new HubException("Session not found or not authorized.");
        }

        _sessionManager.CloseSession(sessionId, reason ?? "closed-by-user", userId);

        await Clients.Group(RemoteDebugGroupNames.ForSession(sessionId))
            .SendAsync("RemoteDebugSessionEnded", new
            {
                sessionId,
                endedAtUtc = DateTime.UtcNow,
                reason = reason ?? "closed-by-user"
            });
    }

    // Canal opcional: o agent pode empurrar logs direto no hub quando estiver conectado via SignalR.
    public async Task PushLog(Guid sessionId, string message, string? level)
    {
        if (Context.Items["AgentId"] is not Guid agentId)
            throw new HubException("Not authenticated as agent.");

        if (!_sessionManager.TryGetSessionForAgent(sessionId, agentId, out _))
            throw new HubException("Session not found or not authorized for this agent.");

        var normalizedLevel = string.IsNullOrWhiteSpace(level) ? "info" : level.Trim().ToLowerInvariant();
        var safeMessage = string.IsNullOrWhiteSpace(message) ? string.Empty : message;
        if (safeMessage.Length > 4096)
            safeMessage = safeMessage[..4096];

        var payload = new
        {
            sessionId,
            agentId,
            level = normalizedLevel,
            message = safeMessage,
            timestampUtc = DateTime.UtcNow,
            sequence = _sessionManager.NextSequence(sessionId),
            transport = "signalr"
        };

        await Clients.Group(RemoteDebugGroupNames.ForSession(sessionId))
            .SendAsync("RemoteDebugLog", payload);

        _sessionManager.Touch(sessionId);
    }
}

public static class RemoteDebugGroupNames
{
    public static string ForSession(Guid sessionId)
        => $"remote-debug:session:{sessionId}";
}
