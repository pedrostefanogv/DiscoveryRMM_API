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

public sealed class GetSessionCredentialsQueryHandler(
    IRemoteSessionManager sessionManager,
    IConfigurationService configurationService,
    INatsCredentialsService credentialsService
) : IRequestHandler<GetSessionCredentialsQuery, Result<SessionCredentialsDto>>
{
    public async Task<Result<SessionCredentialsDto>> Handle(GetSessionCredentialsQuery query, CancellationToken ct)
    {
        var session = await sessionManager.GetActiveForUserAsync(query.SessionId, query.UserId, ct);
        if (session is null)
            return Result<SessionCredentialsDto>.Failure(Error.NotFound("Remote session not found or not active."));

        var serverConfig = await configurationService.GetServerConfigAsync();
        // Normaliza garantindo barra final no path: navegadores NAO seguem
        // redirect (nginx 308 "/nats" -> "/nats/") no handshake de WebSocket,
        // e o viewer do browser e o unico cliente que usa o path da URL.
        var natsWsUrl = !string.IsNullOrWhiteSpace(serverConfig.NatsWebSocketExternalUrl)
            ? Services.NatsWebSocketUrlNormalizer.Normalize(serverConfig.NatsWebSocketExternalUrl)
            : null;

        if (string.IsNullOrWhiteSpace(session.NatsSubject))
            return Result<SessionCredentialsDto>.Failure(Error.Validation("NatsSubject", "Session has no NATS subject configured."));

        // Permissões pub/sub scoped para o viewer da sessão remota.
        // Alinhado com RemoteSessionJwtIssuer.BuildDefaultPermissions.
        var natsSubject = session.NatsSubject;
        var pubSubjects = new[]
        {
            $"{natsSubject}.input",
            $"{natsSubject}.ack",
            $"{natsSubject}.term.in",
            $"{natsSubject}.files.req",
            $"{natsSubject}.proxy.req",
            $"{natsSubject}.proc.req",
            $"{natsSubject}.clipboard.req",
            // Controle do viewer (keyframe ao voltar para a aba). Alinhado com
            // RemoteSessionJwtIssuer.BuildDefaultPermissions — sem isso o NATS
            // rejeita o PUB com -ERR e derruba o WebSocket do viewer.
            $"{natsSubject}.control",
        };
        var subSubjects = new[]
        {
            $"{natsSubject}.frame",
            $"{natsSubject}.frame.frag",
            $"{natsSubject}.cursor",
            $"{natsSubject}.cursor.img",
            $"{natsSubject}.monitors",
            $"{natsSubject}.event",
            $"{natsSubject}.clipboard",
            $"{natsSubject}.term.out",
            $"{natsSubject}.term.ready",
            $"{natsSubject}.files.ready",
            $"{natsSubject}.files.resp",
            $"{natsSubject}.files.progress",
            $"{natsSubject}.proxy.resp",
            $"{natsSubject}.proc.resp",
            $"{natsSubject}.proc.ready",
            // Liveness: o viewer precisa SUBSCREVER o .control para receber
            // ping/pong/closed do agent. Sem isto o SUB e recusado pelo NATS e
            // o viewer nunca ve o pong (sessao morre por viewer-timeout).
            $"{natsSubject}.control",
        };

        // FIX (terminal/acesso remoto — item 1 do plano): TTL da credencial do
        // viewer deve cobrir TODO o tempo restante da sessão + margem. Antes
        // era fixo em 30 min (igual à duração padrão da sessão): sessão
        // renovada, duração > 30 min ou fetch tardio das credenciais fazia o
        // JWT expirar ANTES da sessão — o WebSocket do viewer caía no meio do
        // uso (terminal/tela "morrem sozinhos" com -ERR authorization).
        var remaining = session.ExpiresAt - DateTime.UtcNow;
        var ttlMinutes = remaining > TimeSpan.Zero
            ? (int)Math.Ceiling(remaining.Add(TimeSpan.FromMinutes(10)).TotalMinutes)
            : 30;

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