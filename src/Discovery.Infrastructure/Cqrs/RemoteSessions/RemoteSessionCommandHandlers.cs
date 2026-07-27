using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.RemoteSessions.Commands;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Cqrs.RemoteSessions;

public sealed class StartRemoteSessionCommandHandler : IRequestHandler<StartRemoteSessionCommand, Result<RemoteSessionResponseDto>>
{
    private readonly IRemoteSessionManager _sessionManager;
    private readonly RemoteSessionDispatcher _dispatcher;
    private readonly RemoteSessionJwtIssuer _jwtIssuer;
    private readonly WebrtcTurnCredentialIssuer _turnIssuer;
    private readonly RemoteAccessOptions _options;
    private readonly ILogger<StartRemoteSessionCommandHandler> _logger;

    public StartRemoteSessionCommandHandler(
        IRemoteSessionManager sessionManager,
        RemoteSessionDispatcher dispatcher,
        RemoteSessionJwtIssuer jwtIssuer,
        WebrtcTurnCredentialIssuer turnIssuer,
        IOptions<RemoteAccessOptions> options,
        ILogger<StartRemoteSessionCommandHandler> logger)
    {
        _sessionManager = sessionManager;
        _dispatcher = dispatcher;
        _jwtIssuer = jwtIssuer;
        _turnIssuer = turnIssuer;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<RemoteSessionResponseDto>> Handle(StartRemoteSessionCommand cmd, CancellationToken ct)
    {
        // S4: Validar feature flag
        if (!_options.Enabled)
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

        try
        {
            var natsSubject = $"tenant.{cmd.TenantId}.site.{cmd.SiteId}.agent.{cmd.AgentId}.remote.session.{Guid.NewGuid():N}";
            var natsSubjectBase = $"tenant.{cmd.TenantId}.site.{cmd.SiteId}.agent.{cmd.AgentId}.remote.session";

            var session = await _sessionManager.CreateSessionAsync(
                cmd.AgentId, cmd.UserId, cmd.TenantId, cmd.SiteId,
                cmd.Kind, cmd.Transport, cmd.Quality, cmd.Codec,
                natsSubject, ct);

            // Emite credenciais NATS (JWT + NKey) e TURN
            var permissions = _jwtIssuer.BuildDefaultPermissions(natsSubjectBase);
            var (jwt, nkeySeed) = _jwtIssuer.IssueSessionToken(
                session.Id, cmd.UserId, natsSubjectBase, permissions);

            TurnCredentialsDto? turnCreds = null;
            if (cmd.Transport == Core.Enums.RemoteTransport.Webrtc)
            {
                var (username, credential, urls, ttlSec) = _turnIssuer.IssueCredentials(session.Id.ToString("N"));
                turnCreds = new TurnCredentialsDto(urls, username, credential, ttlSec);
            }

            // Dispara comando para o agent iniciar a sessão
            await _dispatcher.DispatchStartAsync(
                cmd.AgentId, session.Id, cmd.Kind, cmd.Transport,
                cmd.Quality, cmd.Codec, cmd.DurationMinutes,
                session.ExpiresAt, natsSubject, ct);

            _logger.LogInformation("Remote session {SessionId} started for agent {AgentId}", session.Id, cmd.AgentId);

            return Result<RemoteSessionResponseDto>.Success(new RemoteSessionResponseDto(
                session.Id,
                natsSubject,
                cmd.AgentId,
                cmd.Kind.ToString().ToLowerInvariant(),
                cmd.Transport.ToString().ToLowerInvariant(),
                cmd.Quality.ToString().ToLowerInvariant(),
                cmd.Codec.ToString().ToLowerInvariant(),
                "active",
                session.ExpiresAt,
                session.StartedAt,
                null,
                turnCreds));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to start remote session for agent {AgentId}", cmd.AgentId);
            return Result<RemoteSessionResponseDto>.Failure(Error.Conflict(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error starting remote session for agent {AgentId}", cmd.AgentId);
            return Result<RemoteSessionResponseDto>.Failure(Error.Internal("Failed to start remote session."));
        }
    }
}

public sealed class StopRemoteSessionCommandHandler : IRequestHandler<StopRemoteSessionCommand, Result<VoidResult>>
{
    private readonly IRemoteSessionManager _sessionManager;
    private readonly RemoteSessionDispatcher _dispatcher;
    private readonly ILogger<StopRemoteSessionCommandHandler> _logger;

    public StopRemoteSessionCommandHandler(
        IRemoteSessionManager sessionManager,
        RemoteSessionDispatcher dispatcher,
        ILogger<StopRemoteSessionCommandHandler> logger)
    {
        _sessionManager = sessionManager;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<Result<VoidResult>> Handle(StopRemoteSessionCommand cmd, CancellationToken ct)
    {
        try
        {
            await _sessionManager.CloseSessionAsync(cmd.SessionId, "stopped-by-user", cmd.UserId, ct);
            await _dispatcher.DispatchStopAsync(cmd.AgentId, cmd.SessionId, ct);

            _logger.LogInformation("Remote session {SessionId} stopped by user {UserId}", cmd.SessionId, cmd.UserId);
            return Result<VoidResult>.Success(VoidResult.Instance);
        }
        catch (InvalidOperationException ex)
        {
            return Result<VoidResult>.Failure(Error.NotFound(ex.Message));
        }
    }
}

public sealed class RenewRemoteSessionCommandHandler : IRequestHandler<RenewRemoteSessionCommand, Result<RemoteSessionResponseDto>>
{
    private readonly IRemoteSessionManager _sessionManager;
    private readonly ILogger<RenewRemoteSessionCommandHandler> _logger;

    public RenewRemoteSessionCommandHandler(
        IRemoteSessionManager sessionManager,
        ILogger<RenewRemoteSessionCommandHandler> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public async Task<Result<RemoteSessionResponseDto>> Handle(RenewRemoteSessionCommand cmd, CancellationToken ct)
    {
        try
        {
            var session = await _sessionManager.RenewSessionAsync(cmd.SessionId, cmd.UserId, ct);

            return Result<RemoteSessionResponseDto>.Success(new RemoteSessionResponseDto(
                session.Id,
                session.NatsSubject ?? string.Empty,
                session.AgentId,
                session.Kind.ToString().ToLowerInvariant(),
                session.Transport.ToString().ToLowerInvariant(),
                session.QualityProfile.ToString().ToLowerInvariant(),
                session.Codec.ToString().ToLowerInvariant(),
                session.Status,
                session.ExpiresAt,
                session.StartedAt));
        }
        catch (InvalidOperationException ex)
        {
            return Result<RemoteSessionResponseDto>.Failure(Error.NotFound(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<RemoteSessionResponseDto>.Failure(Error.Forbidden(ex.Message));
        }
    }
}

public sealed class AckFrameCommandHandler : IRequestHandler<AckFrameCommand, Result<VoidResult>>
{
    private readonly ILogger<AckFrameCommandHandler> _logger;

    public AckFrameCommandHandler(ILogger<AckFrameCommandHandler> logger)
    {
        _logger = logger;
    }

    public Task<Result<VoidResult>> Handle(AckFrameCommand cmd, CancellationToken ct)
    {
        // Frame ack é apenas métrico; não persiste no banco.
        // As métricas são usadas pelo quality manager no agent.
        _logger.LogDebug("Frame ack: session {SessionId}, seq {Seq}, rtt {RttMs}ms",
            cmd.SessionId, cmd.FrameSeq, cmd.RttMs);

        return Task.FromResult(Result<VoidResult>.Success(VoidResult.Instance));
    }
}

public sealed class StartRecordingCommandHandler : IRequestHandler<StartRecordingCommand, Result<RecordingResponseDto>>
{
    private readonly IRemoteRecordingService _recordingService;
    private readonly RemoteSessionDispatcher _dispatcher;
    private readonly ILogger<StartRecordingCommandHandler> _logger;

    public StartRecordingCommandHandler(
        IRemoteRecordingService recordingService,
        RemoteSessionDispatcher dispatcher,
        ILogger<StartRecordingCommandHandler> logger)
    {
        _recordingService = recordingService;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<Result<RecordingResponseDto>> Handle(StartRecordingCommand cmd, CancellationToken ct)
    {
        try
        {
            var recording = await _recordingService.StartRecordingAsync(cmd.SessionId, cmd.UserId, ct);
            await _dispatcher.DispatchRecordingStartAsync(cmd.AgentId, cmd.SessionId, ct);

            _logger.LogInformation("Recording {RecordingId} started for session {SessionId}",
                recording.Id, cmd.SessionId);

            return Result<RecordingResponseDto>.Success(new RecordingResponseDto(
                recording.Id, cmd.SessionId, recording.Status, recording.StartedAt));
        }
        catch (InvalidOperationException ex)
        {
            return Result<RecordingResponseDto>.Failure(Error.Conflict(ex.Message));
        }
    }
}

public sealed class StopRecordingCommandHandler : IRequestHandler<StopRecordingCommand, Result<RecordingResponseDto>>
{
    private readonly IRemoteRecordingService _recordingService;
    private readonly RemoteSessionDispatcher _dispatcher;
    private readonly ILogger<StopRecordingCommandHandler> _logger;

    public StopRecordingCommandHandler(
        IRemoteRecordingService recordingService,
        RemoteSessionDispatcher dispatcher,
        ILogger<StopRecordingCommandHandler> logger)
    {
        _recordingService = recordingService;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<Result<RecordingResponseDto>> Handle(StopRecordingCommand cmd, CancellationToken ct)
    {
        try
        {
            var recording = await _recordingService.StopRecordingAsync(cmd.SessionId, cmd.UserId, ct);
            await _dispatcher.DispatchRecordingStopAsync(cmd.AgentId, cmd.SessionId, ct);

            _logger.LogInformation("Recording stopped for session {SessionId}", cmd.SessionId);

            return Result<RecordingResponseDto>.Success(new RecordingResponseDto(
                recording.Id, cmd.SessionId, recording.Status, recording.StartedAt,
                recording.StorageUrl));
        }
        catch (InvalidOperationException ex)
        {
            return Result<RecordingResponseDto>.Failure(Error.NotFound(ex.Message));
        }
    }
}
