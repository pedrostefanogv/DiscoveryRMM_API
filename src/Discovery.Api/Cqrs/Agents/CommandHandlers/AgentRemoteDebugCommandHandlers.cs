using System.Text.Json;
using System.Text.Json.Serialization;
using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.RemoteDebug.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Cqrs.Agents.CommandHandlers;

public sealed class StartRemoteDebugCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IRemoteDebugSessionManager sessionManager,
    IAgentCommandDispatcher dispatcher,
    SpecialCommandPayloadValidator payloadValidator,
    IConfigurationService configurationService,
    IOptions<RemoteDebugOptions> debugOptions
) : IRequestHandler<StartRemoteDebugCommand, Result<RemoteDebugResponseDto>>
{
    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<Result<RemoteDebugResponseDto>> Handle(StartRemoteDebugCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<RemoteDebugResponseDto>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return Result<RemoteDebugResponseDto>.Failure(Error.NotFound("Site not found."));

        var options = debugOptions.Value;
        var session = sessionManager.StartSession(cmd.AgentId, cmd.UserId, site.ClientId, agent.SiteId, null, null, null);

        var controlSubject = string.IsNullOrWhiteSpace(session.NatsControlSubject)
            ? NatsSubjectBuilder.RemoteDebugControlSubject(site.ClientId, agent.SiteId, cmd.AgentId)
            : session.NatsControlSubject;

        var maxExpiresAtUtc = session.MaxExpiresAtUtc == DateTime.MaxValue ? (DateTime?)null : session.MaxExpiresAtUtc;

        var payload = JsonSerializer.Serialize(new
        {
            action = "start",
            sessionId = session.SessionId,
            logLevel = session.LogLevel,
            expiresAtUtc = session.ExpiresAtUtc,
            maxExpiresAtUtc,
            liveness = new
            {
                pingIntervalSeconds = options.PingIntervalSeconds,
                missedPingsBeforeClose = options.MissedPingsBeforeClose,
                initialGraceSeconds = options.InitialGraceSeconds,
                keepAliveSeconds = options.KeepAliveSeconds
            },
            stream = new
            {
                natsSubject = session.NatsSubject,
                natsControlSubject = controlSubject
            }
        }, EnvelopeJsonOptions);

        if (!payloadValidator.TryNormalize(CommandType.RemoteDebug, payload, out var normalizedPayload, out var validationError))
            return Result<RemoteDebugResponseDto>.Failure(Error.Validation("Payload", validationError ?? "Invalid remote debug payload."));

        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = CommandType.RemoteDebug, Payload = normalizedPayload };
        await dispatcher.DispatchAsync(command, ct);

        var serverConfig = await configurationService.GetServerConfigAsync();
        // Normaliza garantindo barra final no path: navegadores NAO seguem
        // redirect (nginx 308 "/nats" -> "/nats/") no handshake de WebSocket,
        // e o viewer do browser e o unico cliente que usa o path da URL.
        var natsWsUrl = !string.IsNullOrWhiteSpace(serverConfig.NatsWebSocketExternalUrl)
            ? Services.NatsWebSocketUrlNormalizer.Normalize(serverConfig.NatsWebSocketExternalUrl)
            : null;

        return Result<RemoteDebugResponseDto>.Success(new RemoteDebugResponseDto(
            session.SessionId,
            session.NatsSubject,
            0,
            "started",
            session.AgentId,
            session.ExpiresAtUtc,
            natsWsUrl,
            maxExpiresAtUtc,
            options.PingIntervalSeconds,
            options.MissedPingsBeforeClose,
            options.InitialGraceSeconds,
            options.KeepAliveSeconds,
            controlSubject));
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

/// <summary>
/// Renova o TTL da sessao (keepalive do viewer). A renovacao passa a ser a
/// unica forma de manter a sessao viva: sem ela o cleanup encerra por
/// keepalive-timeout e a sessao nao fica presa quando o navegador morre.
/// </summary>
public sealed class RenewRemoteDebugCommandHandler(
    IRemoteDebugSessionManager sessionManager
) : IRequestHandler<RenewRemoteDebugCommand, Result<RemoteDebugRenewalDto>>
{
    public Task<Result<RemoteDebugRenewalDto>> Handle(RenewRemoteDebugCommand cmd, CancellationToken ct)
    {
        if (!sessionManager.TryRenewSession(cmd.SessionId, cmd.UserId, out var session) || session is null)
            return Task.FromResult(Result<RemoteDebugRenewalDto>.Failure(
                Error.Validation("SessionId", "Remote debug session is not active or reached its maximum duration.")));

        if (session.AgentId != cmd.AgentId)
            return Task.FromResult(Result<RemoteDebugRenewalDto>.Failure(
                Error.Validation("AgentId", "Session does not belong to this agent.")));

        var maxExpiresAtUtc = session.MaxExpiresAtUtc == DateTime.MaxValue ? (DateTime?)null : session.MaxExpiresAtUtc;

        return Task.FromResult(Result<RemoteDebugRenewalDto>.Success(new RemoteDebugRenewalDto(
            session.SessionId,
            session.ExpiresAtUtc,
            maxExpiresAtUtc,
            SessionActive: !session.IsClosed)));
    }
}

/// <summary>
/// Troca o nivel de log da sessao viva SEM reiniciar. O servidor continua dono
/// do estado (e da auditoria) e entrega o comando ao agente pelo canal UNICO de
/// controle (setLevel), no mesmo subject do ping/pong.
/// </summary>
public sealed class SetRemoteDebugLogLevelCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IRemoteDebugSessionManager sessionManager,
    IAgentMessaging messaging
) : IRequestHandler<SetRemoteDebugLogLevelCommand, Result<RemoteDebugLevelDto>>
{
    private const string ControlTypeSetLevel = "setLevel";
    private const string RoleServer = "server";

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<Result<RemoteDebugLevelDto>> Handle(SetRemoteDebugLogLevelCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<RemoteDebugLevelDto>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return Result<RemoteDebugLevelDto>.Failure(Error.NotFound("Site not found."));

        if (!sessionManager.TrySetLogLevel(cmd.SessionId, cmd.UserId, cmd.LogLevel, out var session) || session is null)
            return Result<RemoteDebugLevelDto>.Failure(Error.NotFound("Remote debug session not found."));

        if (session.AgentId != cmd.AgentId)
            return Result<RemoteDebugLevelDto>.Failure(Error.Validation("AgentId", "Session does not belong to this agent."));

        var envelope = JsonSerializer.Serialize(new
        {
            v = 1,
            type = ControlTypeSetLevel,
            sessionId = session.SessionId,
            from = RoleServer,
            sequence = sessionManager.NextSequence(session.SessionId),
            timestampUtc = DateTime.UtcNow.ToString("O"),
            payload = new { logLevel = session.LogLevel }
        }, EnvelopeJsonOptions);

        await messaging.PublishRemoteDebugControlAsync(site.ClientId, session.SiteId, session.AgentId, envelope, ct);

        return Result<RemoteDebugLevelDto>.Success(new RemoteDebugLevelDto(
            session.SessionId,
            session.LogLevel,
            DateTime.UtcNow));
    }
}
