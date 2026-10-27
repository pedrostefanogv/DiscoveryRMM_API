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
        // Só expõe sessões do próprio usuário (mesmo filtro do GetForUserAsync).
        var dtos = sessions
            .Where(s => s.UserId == query.UserId)
            .Select(s => new ActiveSessionDto(
                s.Id, s.AgentId, s.UserId,
                s.Kind.ToString(), s.Transport.ToString(), s.QualityProfile.ToString(),
                s.Codec.ToString(), s.Status,
                s.StartedAt, s.ExpiresAt, s.RecordingEnabled,
                // NatsSubject não é exposto em listagem (só via credenciais da sessão).
                NatsSubject: null));

        return Result<IEnumerable<ActiveSessionDto>>.Success(dtos);
    }
}

public sealed class GetSessionCredentialsQueryHandler(
    IRemoteSessionManager sessionManager,
    IRemoteSessionRepository sessionRepo,
    IConfigurationService configurationService,
    INatsCredentialsService credentialsService
) : IRequestHandler<GetSessionCredentialsQuery, Result<SessionCredentialsDto>>
{
    public async Task<Result<SessionCredentialsDto>> Handle(GetSessionCredentialsQuery query, CancellationToken ct)
    {
        var session = await sessionManager.GetActiveForUserAsync(query.SessionId, query.UserId, ct);
        if (session is null)
            return Result<SessionCredentialsDto>.Failure(Error.NotFound("Remote session not found or not active."));

        // Sinal de vida do VIEWER no servidor: se as credenciais foram emitidas,
        // a sessão não é órfã (o sweeper de sessões sem viewer não a fecha).
        await sessionRepo.TouchActivityAsync(session.Id, DateTime.UtcNow, ct);

        var serverConfig = await configurationService.GetServerConfigAsync();
        // Normaliza garantindo barra final no path: navegadores NAO seguem
        // redirect (nginx 308 "/nats" -> "/nats/") no handshake de WebSocket,
        // e o viewer do browser e o unico cliente que usa o path da URL.
        var natsWsUrl = !string.IsNullOrWhiteSpace(serverConfig.NatsWebSocketExternalUrl)
            ? Services.NatsWebSocketUrlNormalizer.Normalize(serverConfig.NatsWebSocketExternalUrl)
            : null;

        if (string.IsNullOrWhiteSpace(session.NatsSubject))
            return Result<SessionCredentialsDto>.Failure(Error.Validation("NatsSubject", "Session has no NATS subject configured."));

        // Fonte única de pub/sub e TTL (a resposta de start usa a MESMA lista).
        var (pubSubjects, subSubjects) = RemoteSessionViewerPermissions.Build(session.NatsSubject);
        var ttlMinutes = RemoteSessionViewerPermissions.TtlMinutes(session.ExpiresAt);

        var (jwt, nkeySeed, _) = await credentialsService.IssueSessionCredentialsAsync(
            pubSubjects,
            subSubjects,
            ttlMinutes: ttlMinutes,
            $"session:{session.Id:N}",
            ct);

        return Result<SessionCredentialsDto>.Success(new SessionCredentialsDto(
            jwt,
            nkeySeed,
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