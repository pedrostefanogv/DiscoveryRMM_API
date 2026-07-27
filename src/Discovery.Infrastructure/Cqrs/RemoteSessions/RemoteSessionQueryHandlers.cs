using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.RemoteSessions.Commands;
using Discovery.Core.Cqrs.RemoteSessions.Queries;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.RemoteSessions;

public sealed class GetActiveSessionsQueryHandler : IRequestHandler<GetActiveSessionsQuery, Result<IEnumerable<ActiveSessionDto>>>
{
    private readonly IRemoteSessionRepository _repo;
    private readonly ILogger<GetActiveSessionsQueryHandler> _logger;

    public GetActiveSessionsQueryHandler(
        IRemoteSessionRepository repo,
        ILogger<GetActiveSessionsQueryHandler> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<Result<IEnumerable<ActiveSessionDto>>> Handle(GetActiveSessionsQuery query, CancellationToken ct)
    {
        var sessions = await _repo.GetActiveByAgentAsync(query.AgentId, ct);

        var dtos = sessions
            .Where(s => s.Status == "active" && (s.UserId == query.UserId))
            .Select(s => new ActiveSessionDto(
                s.Id,
                s.AgentId,
                s.UserId,
                s.Kind.ToString().ToLowerInvariant(),
                s.Transport.ToString().ToLowerInvariant(),
                s.QualityProfile.ToString().ToLowerInvariant(),
                s.Codec.ToString().ToLowerInvariant(),
                s.Status,
                s.StartedAt,
                s.ExpiresAt,
                s.RecordingEnabled,
                s.NatsSubject))
            .ToList();

        return Result<IEnumerable<ActiveSessionDto>>.Success(dtos);
    }
}

public sealed class GetTurnCredentialsQueryHandler : IRequestHandler<GetTurnCredentialsQuery, Result<TurnCredentialsDto>>
{
    private readonly Discovery.Api.Services.WebrtcTurnCredentialIssuer _turnIssuer;
    private readonly IRemoteSessionRepository _repo;
    private readonly ILogger<GetTurnCredentialsQueryHandler> _logger;

    public GetTurnCredentialsQueryHandler(
        Discovery.Api.Services.WebrtcTurnCredentialIssuer turnIssuer,
        IRemoteSessionRepository repo,
        ILogger<GetTurnCredentialsQueryHandler> logger)
    {
        _turnIssuer = turnIssuer;
        _repo = repo;
        _logger = logger;
    }

    public async Task<Result<TurnCredentialsDto>> Handle(GetTurnCredentialsQuery query, CancellationToken ct)
    {
        var session = await _repo.GetByIdAsync(query.SessionId, ct);
        if (session is null || session.Status != "active")
            return Result<TurnCredentialsDto>.Failure(Error.NotFound("Session not found or not active."));

        if (session.UserId != query.UserId)
            return Result<TurnCredentialsDto>.Failure(Error.Forbidden("Session does not belong to this user."));

        var (username, credential, urls, ttlSec) = _turnIssuer.IssueCredentials(session.Id.ToString("N"));

        return Result<TurnCredentialsDto>.Success(new TurnCredentialsDto(urls, username, credential, ttlSec));
    }
}

public sealed class GetSessionCredentialsQueryHandler : IRequestHandler<GetSessionCredentialsQuery, Result<SessionCredentialsDto>>
{
    private readonly Discovery.Api.Services.RemoteSessionJwtIssuer _jwtIssuer;
    private readonly IRemoteSessionRepository _repo;
    private readonly ILogger<GetSessionCredentialsQueryHandler> _logger;

    public GetSessionCredentialsQueryHandler(
        Discovery.Api.Services.RemoteSessionJwtIssuer jwtIssuer,
        IRemoteSessionRepository repo,
        ILogger<GetSessionCredentialsQueryHandler> logger)
    {
        _jwtIssuer = jwtIssuer;
        _repo = repo;
        _logger = logger;
    }

    public async Task<Result<SessionCredentialsDto>> Handle(GetSessionCredentialsQuery query, CancellationToken ct)
    {
        var session = await _repo.GetByIdAsync(query.SessionId, ct);
        if (session is null || session.Status != "active")
            return Result<SessionCredentialsDto>.Failure(Error.NotFound("Session not found or not active."));

        if (session.UserId != query.UserId)
            return Result<SessionCredentialsDto>.Failure(Error.Forbidden("Session does not belong to this user."));

        var natsSubjectBase = session.NatsSubject ?? string.Empty;
        // Remove o ID da sessão do final para ter o subject base
        var lastDot = natsSubjectBase.LastIndexOf('.');
        var baseSubject = lastDot > 0 ? natsSubjectBase[..lastDot] : natsSubjectBase;

        var permissions = _jwtIssuer.BuildDefaultPermissions(baseSubject);
        var (jwt, nkeySeed) = _jwtIssuer.IssueSessionToken(
            query.SessionId, query.UserId, baseSubject, permissions);

        return Result<SessionCredentialsDto>.Success(new SessionCredentialsDto(
            jwt,
            nkeySeed,
            DateTime.UtcNow.AddMinutes(30),
            null));
    }
}

public sealed class GetRecordingDownloadQueryHandler : IRequestHandler<GetRecordingDownloadQuery, Result<RecordingDownloadDto>>
{
    private readonly IRemoteRecordingService _recordingService;
    private readonly ILogger<GetRecordingDownloadQueryHandler> _logger;

    public GetRecordingDownloadQueryHandler(
        IRemoteRecordingService recordingService,
        ILogger<GetRecordingDownloadQueryHandler> logger)
    {
        _recordingService = recordingService;
        _logger = logger;
    }

    public async Task<Result<RecordingDownloadDto>> Handle(GetRecordingDownloadQuery query, CancellationToken ct)
    {
        try
        {
            var url = await _recordingService.GetDownloadUrlAsync(query.SessionId, ct);
            return Result<RecordingDownloadDto>.Success(new RecordingDownloadDto(
                url, "webm", 0, 0, DateTime.UtcNow.AddMinutes(15)));
        }
        catch (InvalidOperationException ex)
        {
            return Result<RecordingDownloadDto>.Failure(Error.NotFound(ex.Message));
        }
    }
}
