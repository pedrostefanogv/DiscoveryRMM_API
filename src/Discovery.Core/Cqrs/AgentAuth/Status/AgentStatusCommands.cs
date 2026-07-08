using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Status;

public sealed record GetAgentRealtimeStatusQuery : IQuery<Result<AgentRealtimeStatusDto>>;
public sealed record AgentRealtimeStatusDto(
    Guid AgentId, DateTime ServerTimeUtc, bool NatsConnected,
    bool RedisConnected, bool Healthy, DateTime CheckedAtUtc);