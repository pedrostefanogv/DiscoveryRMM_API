using Discovery.Api.Services;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Microsoft.AspNetCore.SignalR;

namespace Discovery.Api.Hubs;

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
            preferredTransport = session.PreferredTransport,
            fallbackTransport = session.FallbackTransport,
            natsSubject = session.NatsSubject,
            signalRMethod = session.SignalRMethod
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
}

public static class RemoteDebugGroupNames
{
    public static string ForSession(Guid sessionId)
        => $"remote-debug:session:{sessionId}";
}
