using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Registers health checks for critical infrastructure dependencies:
/// PostgreSQL, Redis, and NATS.
/// </summary>
public static class HealthChecksServiceCollectionExtensions
{
    public static IServiceCollection AddDiscoveryHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var redisConnString = configuration.GetValue<string>("Redis:Connection") ?? "127.0.0.1:6379";

        var healthChecksBuilder = services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgresql", tags: ["db", "critical"]);

        // Redis health check
        healthChecksBuilder.AddAsyncCheck("redis", async ct =>
        {
            try
            {
                var redis = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(
                    StackExchange.Redis.ConfigurationOptions.Parse(redisConnString));
                await redis.GetDatabase().PingAsync();
                return HealthCheckResult.Healthy("Redis connected");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Redis unavailable", ex);
            }
        }, tags: ["cache"]);

        // NATS health check
        healthChecksBuilder.AddAsyncCheck("nats", async ct =>
        {
            try
            {
                var natsUrl = configuration.GetValue<string>("Nats:Url") ?? "nats://localhost:4222";
                var opts = NATS.Client.Core.NatsOpts.Default with
                {
                    Url = natsUrl,
                    ConnectTimeout = TimeSpan.FromSeconds(3)
                };

                await using var conn = new NATS.Client.Core.NatsConnection(opts);
                await conn.ConnectAsync();
                await conn.PingAsync();

                return HealthCheckResult.Healthy($"NATS connected to {natsUrl}");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("NATS unavailable", ex);
            }
        }, tags: ["messaging"]);

        return services;
    }
}

internal sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly Discovery.Infrastructure.Data.DiscoveryDbContext _db;
    public PostgresHealthCheck(Discovery.Infrastructure.Data.DiscoveryDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _db.Database.CanConnectAsync(cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL connected");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unavailable", ex);
        }
    }
}
