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
