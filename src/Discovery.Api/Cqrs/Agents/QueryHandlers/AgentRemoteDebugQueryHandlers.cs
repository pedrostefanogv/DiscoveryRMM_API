using Discovery.Api.Services;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.RemoteDebug.Queries;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Api.Cqrs.Agents.QueryHandlers;

public sealed class GetRemoteDebugCredentialsQueryHandler(
    IRemoteDebugSessionManager sessionManager,
    IPermissionService permissionService,
    INatsCredentialsService credentialsService,
    IConfigurationService configurationService,
    ILogger<GetRemoteDebugCredentialsQueryHandler> logger
) : IRequestHandler<GetRemoteDebugCredentialsQuery, Result<RemoteDebugCredentialsDto>>
{
    public async Task<Result<RemoteDebugCredentialsDto>> Handle(GetRemoteDebugCredentialsQuery query, CancellationToken ct)
    {
        if (!sessionManager.TryGetSessionForUser(query.SessionId, query.UserId, out var session) || session is null)
            return Result<RemoteDebugCredentialsDto>.Failure(Error.NotFound("Remote debug session not found."));

        if (session.AgentId != query.AgentId)
            return Result<RemoteDebugCredentialsDto>.Failure(Error.Validation("AgentId", "Session does not belong to this agent."));

        if (session.IsClosed)
            return Result<RemoteDebugCredentialsDto>.Failure(Error.Validation("SessionId", "Session is already closed."));

        try
        {
            var scopeAccess = await permissionService.GetScopeAccessAsync(query.UserId, Discovery.Core.Enums.Identity.ResourceType.Agents, Discovery.Core.Enums.Identity.ActionType.Execute);
            var remoteDebugScopeAccess = new UserScopeAccess
            {
                HasGlobalAccess = true // dashboard users need to subscribe to agent remote-debug logs
            };

            var creds = await credentialsService.IssueForUserAsync(
                query.UserId,
                scopeAccess,
                session.ClientId,
                session.SiteId,
                ct,
                remoteDebugScopeAccess);

            var serverConfig = await configurationService.GetServerConfigAsync();
            var natsWsUrl = !string.IsNullOrWhiteSpace(serverConfig.NatsWebSocketExternalUrl)
                ? serverConfig.NatsWebSocketExternalUrl
                : null;

            sessionManager.Touch(query.SessionId);

            return Result<RemoteDebugCredentialsDto>.Success(new RemoteDebugCredentialsDto(
                creds.Jwt,
                creds.NkeySeed,
                creds.ExpiresAtUtc,
                natsWsUrl));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to issue NATS credentials for remote debug session {SessionId}", query.SessionId);
            return Result<RemoteDebugCredentialsDto>.Failure(Error.Validation("Nats", "NATS auth is not enabled or not configured."));
        }
    }
}
