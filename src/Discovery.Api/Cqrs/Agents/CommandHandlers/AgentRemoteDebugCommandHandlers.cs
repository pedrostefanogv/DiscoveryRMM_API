using System.Text.Json;
using Discovery.Api.Services;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.RemoteDebug.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Api.Cqrs.Agents.CommandHandlers;

public sealed class StartRemoteDebugCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IRemoteDebugSessionManager sessionManager,
    IAgentCommandDispatcher dispatcher,
    SpecialCommandPayloadValidator payloadValidator,
    IConfigurationService configurationService
) : IRequestHandler<StartRemoteDebugCommand, Result<RemoteDebugResponseDto>>
{
    public async Task<Result<RemoteDebugResponseDto>> Handle(StartRemoteDebugCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<RemoteDebugResponseDto>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return Result<RemoteDebugResponseDto>.Failure(Error.NotFound("Site not found."));

        var session = sessionManager.StartSession(cmd.AgentId, cmd.UserId, site.ClientId, agent.SiteId, null, null, null);

        var payload = JsonSerializer.Serialize(new
        {
            action = "start",
            sessionId = session.SessionId,
            logLevel = session.LogLevel,
            expiresAtUtc = session.ExpiresAtUtc,
            stream = new { natsSubject = session.NatsSubject }
        });

        if (!payloadValidator.TryNormalize(CommandType.RemoteDebug, payload, out var normalizedPayload, out var validationError))
            return Result<RemoteDebugResponseDto>.Failure(Error.Validation("Payload", validationError ?? "Invalid remote debug payload."));

        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = CommandType.RemoteDebug, Payload = normalizedPayload };
        await dispatcher.DispatchAsync(command, ct);

        var serverConfig = await configurationService.GetServerConfigAsync();
        var natsWsUrl = !string.IsNullOrWhiteSpace(serverConfig.NatsWebSocketExternalUrl)
            ? serverConfig.NatsWebSocketExternalUrl
            : null;

        return Result<RemoteDebugResponseDto>.Success(new RemoteDebugResponseDto(
            session.SessionId,
            session.NatsSubject,
            0,
            "started",
            session.AgentId,
            session.ExpiresAtUtc,
            natsWsUrl));
    }
}

public sealed class StopRemoteDebugCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IRemoteDebugSessionManager sessionManager,
    IAgentCommandDispatcher dispatcher,
    SpecialCommandPayloadValidator payloadValidator
) : IRequestHandler<StopRemoteDebugCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(StopRemoteDebugCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return Result<VoidResult>.Failure(Error.NotFound("Site not found."));

        if (!sessionManager.TryGetSessionForUser(cmd.SessionId, cmd.UserId, out var session) || session is null)
            return Result<VoidResult>.Failure(Error.NotFound("Remote debug session not found."));

        if (session.AgentId != cmd.AgentId)
            return Result<VoidResult>.Failure(Error.Validation("AgentId", "Session does not belong to this agent."));

        var payload = JsonSerializer.Serialize(new
        {
            action = "stop",
            sessionId = cmd.SessionId,
            stream = new { natsSubject = session.NatsSubject }
        });

        if (!payloadValidator.TryNormalize(CommandType.RemoteDebug, payload, out var normalizedPayload, out var validationError))
            return Result<VoidResult>.Failure(Error.Validation("Payload", validationError ?? "Invalid remote debug payload."));

        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = CommandType.RemoteDebug, Payload = normalizedPayload };
        await dispatcher.DispatchAsync(command, ct);
        sessionManager.CloseSession(cmd.SessionId, "stopped-by-user", cmd.UserId);

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}