using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Status;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetAgentRealtimeStatusHandler(
    IRedisService redis,
    IServiceProvider serviceProvider
) : IRequestHandler<GetAgentRealtimeStatusQuery, Result<AgentRealtimeStatusDto>>
{
    public Task<Result<AgentRealtimeStatusDto>> Handle(GetAgentRealtimeStatusQuery q, CancellationToken ct)
    {
        var messaging = serviceProvider.GetService<IAgentMessaging>();
        var natsConnected = messaging?.IsConnected == true;
        var redisConnected = redis.IsConnected;
        return Task.FromResult(Result<AgentRealtimeStatusDto>.Success(new AgentRealtimeStatusDto(
            Guid.Empty, DateTime.UtcNow, natsConnected, redisConnected,
            natsConnected && redisConnected, DateTime.UtcNow)));
    }
}