using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Realtime.Queries;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Realtime;

public sealed class GetRealtimeStatusQueryHandler : IRequestHandler<GetRealtimeStatusQuery, Result<RealtimeStatusDto>>
{
    public Task<Result<RealtimeStatusDto>> Handle(GetRealtimeStatusQuery q, CancellationToken ct)
    {
        return Task.FromResult(Result<RealtimeStatusDto>.Success(new RealtimeStatusDto(0, 0, null)));
    }
}

public sealed class GetRealtimeStatsQueryHandler : IRequestHandler<GetRealtimeStatsQuery, Result<RealtimeStatsDto>>
{
    public Task<Result<RealtimeStatsDto>> Handle(GetRealtimeStatsQuery q, CancellationToken ct)
    {
        var stats = new RealtimeStatsDto(
            DateTime.UtcNow,
            new { },
            new { natsConnected = false, redisConnected = false },
            new { connected = false },
            new { },
            new { },
            new { available = false }
        );
        return Task.FromResult(Result<RealtimeStatsDto>.Success(stats));
    }
}
