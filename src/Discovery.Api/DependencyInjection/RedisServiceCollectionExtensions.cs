using Discovery.Api.Services;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using StackExchange.Redis;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Registers Redis cache infrastructure: connection multiplexer and cache-backed services.
/// </summary>
public static class RedisServiceCollectionExtensions
{
    public static IServiceCollection AddDiscoveryRedis(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var redisConnString = configuration.GetValue<string>("Redis:Connection") ?? "127.0.0.1:6379";

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redisConnString);
            var redisPassword = configuration.GetValue<string>("Redis:Password");
            if (!string.IsNullOrWhiteSpace(redisPassword))
            {
                options.Password = redisPassword;
            }

            options.AbortOnConnectFail = false;
            options.ConnectTimeout = 5000;
            options.AsyncTimeout = 5000;
            options.SyncTimeout = 5000;
            options.ConnectRetry = 5;
            options.KeepAlive = 30;
            options.ReconnectRetryPolicy = new ExponentialRetry(5000);
            options.ClientName = "discovery-api";
            return ConnectionMultiplexer.Connect(options);
        });

        services.AddSingleton<IRedisService, RedisService>();
        services.AddSingleton<IHeartbeatCacheService, HeartbeatCacheService>();
        services.AddHostedService<HeartbeatExpiryBackgroundService>();

        return services;
    }
}
