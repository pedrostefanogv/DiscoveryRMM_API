using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Realtime.Queries;

public sealed record GetRealtimeStatusQuery : IQuery<Result<RealtimeStatusDto>>;
public sealed record GetRealtimeStatsQuery : IQuery<Result<RealtimeStatsDto>>;
public sealed record RealtimeStatusDto(int ActiveConnections, int QueuedMessages, DateTime? LastHeartbeat);
public sealed record RealtimeStatsDto(
    DateTime? CheckedAtUtc,
    object Application,
    object Realtime,
    object Database,
    object ProcessMetrics,
    object ThreadPool,
    object Business
);
