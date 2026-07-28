using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.RemoteSessions.Commands;
using Discovery.Core.Cqrs.RemoteSessions.Queries;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Cqrs.RemoteSessions.QueryHandlers;

public sealed class GetActiveSessionsQueryHandler(
    IRemoteSessionRepository repo
) : IRequestHandler<GetActiveSessionsQuery, Result<IEnumerable<ActiveSessionDto>>>
{
    public async Task<Result<IEnumerable<ActiveSessionDto>>> Handle(GetActiveSessionsQuery query, CancellationToken ct)
    {
        var sessions = await repo.GetActiveByAgentAsync(query.AgentId, ct);
        var dtos = sessions.Select(s => new ActiveSessionDto(
            s.Id, s.AgentId, s.UserId,
            s.Kind.ToString(), s.Transport.ToString(), s.QualityProfile.ToString(),
            s.Codec.ToString(), s.Status,
            s.StartedAt, s.ExpiresAt, s.RecordingEnabled, s.NatsSubject));

        return Result<IEnumerable<ActiveSessionDto>>.Success(dtos);
    }
}

public sealed class GetTurnCredentialsQueryHandler(
    IRemoteSessionManager sessionManager,
    IOptions<RemoteAccessOptions> options
) : IRequestHandler<GetTurnCredentialsQuery, Result<TurnCredentialsDto>>
{
    public async Task<Result<TurnCredentialsDto>> Handle(GetTurnCredentialsQuery query, CancellationToken ct)
    {
        var session = await sessionManager.GetActiveForUserAsync(query.SessionId, query.UserId, ct);
        if (session is null)
            return Result<TurnCredentialsDto>.Failure(Error.NotFound("Remote session not found or not active."));

        var webRtc = options.Value.WebRtc;

        // Se WebRTC está desabilitado ou sem TURN configurado, retorna sucesso vazio
        // (o frontend usa NATS como transporte primário e TURN é opcional)
        if (!webRtc.Enabled || webRtc.TurnUrls.Length == 0)
            return Result<TurnCredentialsDto>.Success(new TurnCredentialsDto(
                Array.Empty<string>(),
                string.Empty,
                string.Empty,
                0));

        // Generate HMAC-based credentials for coturn (long-term credential mechanism)
        var ttl = TimeSpan.FromMinutes(webRtc.TurnCredentialTtlMinutes);
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();
        var username = $"{expiresAt}:{query.SessionId:N}";
        var credential = Convert.ToBase64String(Guid.NewGuid().ToByteArray()); // simple random credential; replace with HMAC in production

        return Result<TurnCredentialsDto>.Success(new TurnCredentialsDto(
            webRtc.TurnUrls,
            username,
            credential,
            (int)ttl.TotalSeconds));
    }
}

public sealed class GetSessionCredentialsQueryHandler(
    IRemoteSessionManager sessionManager,
    IConfigurationService configurationService
) : IRequestHandler<GetSessionCredentialsQuery, Result<SessionCredentialsDto>>
{
    public async Task<Result<SessionCredentialsDto>> Handle(GetSessionCredentialsQuery query, CancellationToken ct)
    {
        var session = await sessionManager.GetActiveForUserAsync(query.SessionId, query.UserId, ct);
        if (session is null)
            return Result<SessionCredentialsDto>.Failure(Error.NotFound("Remote session not found or not active."));

        var serverConfig = await configurationService.GetServerConfigAsync();
        var natsWsUrl = !string.IsNullOrWhiteSpace(serverConfig.NatsWebSocketExternalUrl)
            ? serverConfig.NatsWebSocketExternalUrl
            : null;

        // NATS credentials são emitidas pelo serviço de credenciais já existente.
        // Aqui retornamos apenas o URL do WebSocket; o JWT será emitido via endpoint já existente.
        return Result<SessionCredentialsDto>.Success(new SessionCredentialsDto(
            string.Empty, // JWT will be issued by existing NatsCredentialsService
            string.Empty,
            session.ExpiresAt,
            natsWsUrl));
    }
}

public sealed class GetRecordingDownloadQueryHandler(
    IRemoteSessionManager sessionManager,
    IRemoteRecordingService recordingService
) : IRequestHandler<GetRecordingDownloadQuery, Result<RecordingDownloadDto>>
{
    public async Task<Result<RecordingDownloadDto>> Handle(GetRecordingDownloadQuery query, CancellationToken ct)
    {
        var session = await sessionManager.GetActiveForUserAsync(query.SessionId, query.UserId, ct);
        if (session is null)
            return Result<RecordingDownloadDto>.Failure(Error.NotFound("Remote session not found or not active."));

        try
        {
            var url = await recordingService.GetDownloadUrlAsync(query.SessionId, ct);
            return Result<RecordingDownloadDto>.Success(new RecordingDownloadDto(
                url, "webm", 0, 0, DateTime.UtcNow.AddMinutes(15)));
        }
        catch (InvalidOperationException ex)
        {
            return Result<RecordingDownloadDto>.Failure(Error.NotFound(ex.Message));
        }
    }
}
