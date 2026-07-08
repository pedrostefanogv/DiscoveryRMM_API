using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Realtime.Queries;

public sealed record GetRealtimeStatusQuery : IQuery<Result<RealtimeStatusDto>>;
public sealed record RealtimeStatusDto(int ActiveConnections, int QueuedMessages, DateTime? LastHeartbeat);
