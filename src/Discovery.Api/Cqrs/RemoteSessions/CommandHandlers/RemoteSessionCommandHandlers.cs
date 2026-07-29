using System.Text.Json;
using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.RemoteSessions.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services.Remote;
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
        // S4: Validar feature flag
        if (!options.Value.Enabled)
            return Result<RemoteSessionResponseDto>.Failure(Error.Forbidden("Remote access is disabled."));

        // S5: Validar enums
        if (!Enum.IsDefined(cmd.Kind))
            return Result<RemoteSessionResponseDto>.Failure(Error.Validation("kind", $"Invalid value: {cmd.Kind}"));
        if (!Enum.IsDefined(cmd.Transport))
            return Result<RemoteSessionResponseDto>.Failure(Error.Validation("transport", $"Invalid value: {cmd.Transport}"));
        if (!Enum.IsDefined(cmd.Quality))
            return Result<RemoteSessionResponseDto>.Failure(Error.Validation("quality", $"Invalid value: {cmd.Quality}"));
        if (!Enum.IsDefined(cmd.Codec))
            return Result<RemoteSessionResponseDto>.Failure(Error.Validation("codec", $"Invalid value: {cmd.Codec}"));

        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<RemoteSessionResponseDto>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return Result<RemoteSessionResponseDto>.Failure(Error.NotFound("Site not found."));

        // Deriva tenantId/siteId do agente se não vieram populados (caso de ScopeSource.Global)
        var tenantId = cmd.TenantId != Guid.Empty ? cmd.TenantId : site.ClientId;
        var siteId = cmd.SiteId != Guid.Empty ? cmd.SiteId : agent.SiteId;

        // Diagnóstico: logar UserId recebido para rastrear sessões órfãs
        logger.LogInformation(
            "[RemoteSession] StartSession diagnóstico: UserId={UserId}, AgentId={AgentId}, TenantId={TenantId}, SiteId={SiteId}, Kind={Kind}, Transport={Transport}, Force={Force}",
            cmd.UserId, cmd.AgentId, tenantId, siteId, cmd.Kind, cmd.Transport, cmd.Force);

        if (cmd.UserId == Guid.Empty)
        {
            logger.LogError("[RemoteSession] StartSession: UserId é Guid.Empty — sessão será criada como órfã! AgentId={AgentId}", cmd.AgentId);
            return Result<RemoteSessionResponseDto>.Failure(Error.Unauthorized("UserId is required to create a session."));
        }

        // Se Force=true, mata sessões ativas do agent (incluindo órfãs com UserId vazio)
        if (cmd.Force)
        {
            var existingSessions = await sessionManager.GetActiveSessionsForAgentAsync(cmd.AgentId, ct);
            foreach (var existing in existingSessions)
            {
                logger.LogWarning(
                    "[RemoteSession] Force=true: encerrando sessão existente {SessionId} (UserId={SessionUserId}, Status={Status}) para agent {AgentId}",
                    existing.Id, existing.UserId, existing.Status, cmd.AgentId);
                await sessionManager.CloseSessionAsync(existing.Id, "overridden-by-new-session", cmd.UserId, ct);
            }
        }

        RemoteSession session;
        try
        {
            // Subject temporário — será substituído pelo canônico após obter sessionId
            session = await sessionManager.CreateSessionAsync(
                cmd.AgentId, cmd.UserId, tenantId, siteId,
                cmd.Kind, cmd.Transport, cmd.Quality, cmd.Codec, "temp", cmd.Force, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Result<RemoteSessionResponseDto>.Failure(Error.Validation("Session", ex.Message));
        }

        // Subject canônico conforme contrato de comunicação v4.5.0:
        // tenant.{clientId}.site.{siteId}.agent.{agentId}.remote.session.{sessionId}
        var natsSubject = $"tenant.{tenantId:N}.site.{siteId:N}.agent.{cmd.AgentId:N}.remote.session.{session.Id:N}";
        session = await sessionManager.SetNatsSubjectAsync(session.Id, natsSubject, ct);

        logger.LogInformation(
            "[RemoteSession] Subject NATS canônico gerado: {Subject} para SessionId={SessionId}",
            natsSubject, session.Id);

        var (defaultFps, _, defaultJpegQ, _) = QualityProfileMapping.GetParameters(cmd.Quality);

        var payload = JsonSerializer.Serialize(new
        {
            action = "start",
            sessionId = session.Id,
            kind = cmd.Kind.ToString().ToLowerInvariant(),
            transport = cmd.Transport.ToString().ToLowerInvariant(),
            quality = cmd.Quality.ToString().ToLowerInvariant(),
            codec = cmd.Codec.ToString().ToLowerInvariant(),
            imageQuality = defaultJpegQ,
            maxFps = defaultFps,
            durationMinutes = cmd.DurationMinutes,
            expiresAtUtc = session.ExpiresAt,
            natsSubject
        });

        if (!payloadValidator.TryNormalize(CommandType.RemoteSessionStart, payload, out var normalizedPayload, out var validationError))
            return Result<RemoteSessionResponseDto>.Failure(Error.Validation("Payload", validationError ?? "Invalid remote session payload."));

        var command = new AgentCommand
        {
            AgentId = cmd.AgentId,
            CommandType = CommandType.RemoteSessionStart,
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

        if (!payloadValidator.TryNormalize(CommandType.RemoteSessionStop, payload, out var normalizedPayload, out var validationError))
            return Result<VoidResult>.Failure(Error.Validation("Payload", validationError ?? "Invalid remote session payload."));

        var command = new AgentCommand
        {
            AgentId = cmd.AgentId,
            CommandType = CommandType.RemoteSessionStop,
            Payload = normalizedPayload
        };
        await dispatcher.DispatchAsync(command, ct);

        await sessionManager.CloseSessionAsync(cmd.SessionId, "stopped-by-user", cmd.UserId, ct);

        logger.LogInformation("Remote session {SessionId} stopped by user {UserId}", cmd.SessionId, cmd.UserId);

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class RenewRemoteSessionCommandHandler(
    IRemoteSessionManager sessionManager
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

public sealed class AckFrameCommandHandler(
    SessionMetricsStore metricsStore,
    ILogger<AckFrameCommandHandler> logger
) : IRequestHandler<AckFrameCommand, Result<VoidResult>>
{
    public Task<Result<VoidResult>> Handle(AckFrameCommand cmd, CancellationToken ct)
    {
        // Alimenta o store de métricas para AdaptiveQualityService
        metricsStore.RecordFrame(cmd.SessionId, cmd.FrameSeq, cmd.RttMs, cmd.JitterMs, cmd.EstimatedBandwidthKbps);

        logger.LogDebug("Frame ack: session {SessionId}, seq {Seq}, rtt {RttMs}ms, bw {Bw}kbps",
            cmd.SessionId, cmd.FrameSeq, cmd.RttMs, cmd.EstimatedBandwidthKbps);
        return Task.FromResult(Result<VoidResult>.Success(VoidResult.Value));
    }
}

public sealed class StartRecordingCommandHandler(
    IRemoteSessionManager sessionManager,
    IRemoteRecordingService recordingService,
    IAgentCommandDispatcher dispatcher,
    SpecialCommandPayloadValidator payloadValidator,
    ILogger<StartRecordingCommandHandler> logger
) : IRequestHandler<StartRecordingCommand, Result<RecordingResponseDto>>
{
    public async Task<Result<RecordingResponseDto>> Handle(StartRecordingCommand cmd, CancellationToken ct)
    {
        var session = await sessionManager.GetActiveForUserAsync(cmd.SessionId, cmd.UserId, ct);
        if (session is null)
            return Result<RecordingResponseDto>.Failure(Error.NotFound("Remote session not found or not active."));

        try
        {
            var recording = await recordingService.StartRecordingAsync(cmd.SessionId, cmd.UserId, ct);

            var payload = JsonSerializer.Serialize(new { action = "recording_start", sessionId = cmd.SessionId });
            if (payloadValidator.TryNormalize(CommandType.RecordingStart, payload, out var normalizedPayload, out _))
            {
                var command = new AgentCommand
                {
                    AgentId = cmd.AgentId,
                    CommandType = CommandType.RecordingStart,
                    Payload = normalizedPayload
                };
                await dispatcher.DispatchAsync(command, ct);
            }

            logger.LogInformation("Recording {RecordingId} started for session {SessionId}",
                recording.Id, cmd.SessionId);

            return Result<RecordingResponseDto>.Success(new RecordingResponseDto(
                recording.Id, cmd.SessionId, recording.Status, recording.StartedAt));
        }
        catch (InvalidOperationException ex)
        {
            return Result<RecordingResponseDto>.Failure(Error.Validation("Recording", ex.Message));
        }
    }
}

public sealed class StopRecordingCommandHandler(
    IRemoteSessionManager sessionManager,
    IRemoteRecordingService recordingService,
    IAgentCommandDispatcher dispatcher,
    SpecialCommandPayloadValidator payloadValidator,
    ILogger<StopRecordingCommandHandler> logger
) : IRequestHandler<StopRecordingCommand, Result<RecordingResponseDto>>
{
    public async Task<Result<RecordingResponseDto>> Handle(StopRecordingCommand cmd, CancellationToken ct)
    {
        var session = await sessionManager.GetActiveForUserAsync(cmd.SessionId, cmd.UserId, ct);
        if (session is null)
            return Result<RecordingResponseDto>.Failure(Error.NotFound("Remote session not found or not active."));

        try
        {
            var recording = await recordingService.StopRecordingAsync(cmd.SessionId, cmd.UserId, ct);

            var payload = JsonSerializer.Serialize(new { action = "recording_stop", sessionId = cmd.SessionId });
            if (payloadValidator.TryNormalize(CommandType.RecordingStop, payload, out var normalizedPayload, out _))
            {
                var command = new AgentCommand
                {
                    AgentId = cmd.AgentId,
                    CommandType = CommandType.RecordingStop,
                    Payload = normalizedPayload
                };
                await dispatcher.DispatchAsync(command, ct);
            }

            logger.LogInformation("Recording stopped for session {SessionId}", cmd.SessionId);

            return Result<RecordingResponseDto>.Success(new RecordingResponseDto(
                recording.Id, cmd.SessionId, recording.Status, recording.StartedAt, recording.StorageUrl));
        }
        catch (InvalidOperationException ex)
        {
            return Result<RecordingResponseDto>.Failure(Error.NotFound(ex.Message));
        }
    }
}

public sealed class ChangeRemoteSessionQualityCommandHandler(
    IRemoteSessionManager sessionManager,
    RemoteSessionDispatcher dispatcher,
    SessionMetricsStore metricsStore,
    ILogger<ChangeRemoteSessionQualityCommandHandler> logger
) : IRequestHandler<ChangeRemoteSessionQualityCommand, Result<RemoteSessionResponseDto>>
{
    public async Task<Result<RemoteSessionResponseDto>> Handle(ChangeRemoteSessionQualityCommand cmd, CancellationToken ct)
    {
        var session = await sessionManager.GetActiveForUserAsync(cmd.SessionId, cmd.UserId, ct);
        if (session is null)
            return Result<RemoteSessionResponseDto>.Failure(Error.NotFound("Remote session not found or not active."));

        if (session.AgentId != cmd.AgentId)
            return Result<RemoteSessionResponseDto>.Failure(Error.Validation("AgentId", "Session does not belong to this agent."));

        // Valida compatibilidade codec x transporte
        var effectiveCodec = cmd.Codec ?? session.Codec;
        if (!QualityProfileMapping.IsCodecValidForTransport(effectiveCodec, session.Transport))
        {
            return Result<RemoteSessionResponseDto>.Failure(
                Error.Validation("Codec", $"Codec {effectiveCodec} is not supported with transport {session.Transport}. Use JPEG or WebP for NATS, or switch to WebRTC for H264."));
        }

        // Gerencia modo Auto no metrics store
        if (cmd.Auto)
        {
            metricsStore.EnableAutoMode(cmd.SessionId);
            logger.LogInformation("Auto quality mode ENABLED for session {SessionId}", cmd.SessionId);
        }
        else
        {
            metricsStore.DisableAutoMode(cmd.SessionId);
        }

        // Obtém parâmetros do perfil como fallback caso ImageQuality/MaxFps não sejam especificados
        var (defaultFps, scale, jpegQ, webpQ) = QualityProfileMapping.GetParameters(cmd.Quality);

        // Prioriza valores explícitos sobre o perfil
        var effectiveImageQuality = cmd.ImageQuality ?? jpegQ;
        var effectiveMaxFps = cmd.MaxFps ?? defaultFps;

        // Atualiza no banco com valores separados
        session = await sessionManager.UpdateQualityAsync(cmd.SessionId, cmd.Quality, effectiveCodec, effectiveImageQuality, effectiveMaxFps, ct);

        // Dispara comando quality para o agent com imageQuality e maxFps separados
        await dispatcher.DispatchQualityChangeAsync(
            cmd.AgentId, cmd.SessionId, cmd.Quality, effectiveCodec, effectiveImageQuality, effectiveMaxFps, ct);

        logger.LogInformation(
            "Quality changed to {Quality}/{Codec} imageQ={ImageQuality}% maxFps={MaxFps} (scale:{Scale}%) auto={Auto} for session {SessionId}",
            cmd.Quality, effectiveCodec, effectiveImageQuality, effectiveMaxFps, scale, cmd.Auto, cmd.SessionId);

        return Result<RemoteSessionResponseDto>.Success(new RemoteSessionResponseDto(
            session.Id, session.NatsSubject ?? "", session.AgentId,
            session.Kind.ToString(), session.Transport.ToString(),
            session.QualityProfile.ToString(), session.Codec.ToString(),
            session.Status, session.ExpiresAt, session.StartedAt));
    }
}
