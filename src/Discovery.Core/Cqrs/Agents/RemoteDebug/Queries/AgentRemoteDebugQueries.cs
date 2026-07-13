using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.RemoteDebug.Queries;

public sealed record GetRemoteDebugCredentialsQuery(Guid AgentId, Guid SessionId, Guid UserId) : IQuery<Result<RemoteDebugCredentialsDto>>;

public sealed record RemoteDebugCredentialsDto(
    string Jwt,
    string NkeySeed,
    DateTime ExpiresAtUtc,
    string? NatsWssUrl);
