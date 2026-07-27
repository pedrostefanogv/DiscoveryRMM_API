using System.Text.Json;
using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.RemoteSessions.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Cqrs.RemoteSessions.CommandHandlers;

public sealed class StartRemoteSessionCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IRemoteSessionManager sessionManager,
    IAgentCommandDispatcher dispatcher,
    SpecialCommandPayloadValidator payloadValidator,
    IConfigurationService configurationService,
    IOptions<RemoteAccessOptions> options,
    ILogger<StartRemoteSessionCommandHandler> logger
) : IRequestHandler<StartRemoteSessionCommand, Result<RemoteSessionResponseDto>>
{
    public async Task<Result<RemoteSessionResponseDto>> Handle(StartRemoteSessionCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<RemoteSessionResponseDto>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return Result<RemoteSessionResponseDto>.Failure(Error.NotFound("Site not found."));

        var natsSubject = $"{options.Value.Nats.FrameSubjectPrefix}.{cmd.AgentId}.{Guid.NewGuid():N}";

        RemoteSession session;
        try
        {
            session = await sessionManager.CreateSessionAsync(
                cmd.AgentId, cmd.UserId, cmd.TenantId, cmd.SiteId,
                cmd.Kind, cmd.Transport, cmd.Quality, cmd.Codec, natsSubject, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Result<RemoteSessionResponseDto>.Failure(Error.Validation("Session", ex.Message));
        }

        var payload = JsonSerializer.Serialize(new
        {
            action = "start",
            sessionId = session.Id,
            kind = cmd.Kind.ToString().ToLowerInvariant(),
            transport = cmd.Transport.ToString().ToLowerInvariant(),
            quality = cmd.Quality.ToString().ToLowerInvariant(),
            codec = cmd.Codec.ToString().ToLowerInvariant(),
            durationMinutes = cmd.DurationMinutes,
            expiresAtUtc = session.ExpiresAt,
            natsSubject
        });

        if (!payloadValidator.TryNormalize(CommandType.RemoteDebug, payload, out var normalizedPayload, out var validationError))
            return Result<RemoteSessionResponseDto>.Failure(Error.Validation("Payload", validationError ?? "Invalid remote session payload."));

        var command = new AgentCommand
        {
            AgentId = cmd.AgentId,
            CommandType = CommandType.RemoteDebug, // using RemoteDebug as transport for now; will add RemoteSessionStart when wire mapper updated
            Payload = normalizedPayload
        };
        await dispatcher.DispatchAsync(command, ct);

        var serverConfig = await configurationService.GetServerConfigAsync();
        var natsWsUrl = !string.IsNullOrWhiteSpace(serverConfig.NatsWebSocketExternalUrl)
            ? serverConfig.NatsWebSocketExternalUrl
            : null;

        logger.LogInformation("Remote session {SessionId} started for agent {AgentId}", session.Id, cmd.AgentId);

        return Result<RemoteSessionResponseDto>.Success(new RemoteSessionResponseDto(
            session.Id,
            natsSubject,
            session.AgentId,
            session.Kind.ToString(),
            session.Transport.ToString(),
            session.QualityProfile.ToString(),
            session.Codec.ToString(),
            session.Status,
            session.ExpiresAt,
            session.StartedAt,
            natsWsUrl));
    }
}

public sealed class StopRemoteSessionCommandHandler(
    IAgentRepository agentRepo,
    IRemoteSessionManager sessionManager,
    IAgentCommandDispatcher dispatcher,
    SpecialCommandPayloadValidator payloadValidator,
    ILogger<StopRemoteSessionCommandHandler> logger
) : IRequestHandler<StopRemoteSessionCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(StopRemoteSessionCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        var session = await sessionManager.GetActiveForUserAsync(cmd.SessionId, cmd.UserId, ct);
        if (session is null) return Result<VoidResult>.Failure(Error.NotFound("Remote session not found or not active."));

        if (session.AgentId != cmd.AgentId)
            return Result<VoidResult>.Failure(Error.Validation("AgentId", "Session does not belong to this agent."));

        var payload = JsonSerializer.Serialize(new
        {
            action = "stop",
            sessionId = cmd.SessionId
        });

        if (!payloadValidator.TryNormalize(CommandType.RemoteDebug, payload, out var normalizedPayload, out var validationError))
            return Result<VoidResult>.Failure(Error.Validation("Payload", validationError ?? "Invalid remote session payload."));

        var command = new AgentCommand
        {
            AgentId = cmd.AgentId,
            CommandType = CommandType.RemoteDebug,
            Payload = normalizedPayload
        };
        await dispatcher.DispatchAsync(command, ct);

        await sessionManager.CloseSessionAsync(cmd.SessionId, "stopped-by-user", cmd.UserId, ct);

        logger.LogInformation("Remote session {SessionId} stopped by user {UserId}", cmd.SessionId, cmd.UserId);

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class RenewRemoteSessionCommandHandler(
    IRemoteSessionManager sessionManager,
    ILogger<RenewRemoteSessionCommandHandler> logger
) : IRequestHandler<RenewRemoteSessionCommand, Result<RemoteSessionResponseDto>>
{
    public async Task<Result<RemoteSessionResponseDto>> Handle(RenewRemoteSessionCommand cmd, CancellationToken ct)
    {
        try
        {
            var session = await sessionManager.RenewSessionAsync(cmd.SessionId, cmd.UserId, ct);

            return Result<RemoteSessionResponseDto>.Success(new RemoteSessionResponseDto(
                session.Id,
                session.NatsSubject ?? string.Empty,
                session.AgentId,
                session.Kind.ToString(),
                session.Transport.ToString(),
                session.QualityProfile.ToString(),
                session.Codec.ToString(),
                session.Status,
                session.ExpiresAt,
                session.StartedAt));
        }
        catch (InvalidOperationException ex)
        {
            return Result<RemoteSessionResponseDto>.Failure(Error.Validation("Session", ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<RemoteSessionResponseDto>.Failure(Error.Forbidden(ex.Message));
        }
    }
}
