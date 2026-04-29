namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Registers output caching with Redis-backed storage for high-frequency read endpoints.
/// </summary>
public static class OutputCacheServiceCollectionExtensions
{
    public static IServiceCollection AddDiscoveryOutputCache(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var redisConnString = configuration.GetValue<string>("Redis:Connection") ?? "127.0.0.1:6379";

        services.AddOutputCache(options =>
        {
            options.DefaultExpirationTimeSpan = TimeSpan.FromSeconds(30);
            options.MaximumBodySize = 1024 * 64; // 64 KB

            options.AddBasePolicy(builder => builder
                .Expire(TimeSpan.FromSeconds(15))
                .SetVaryByHost(true));

            // Named policies for different cache durations
            options.AddPolicy("Short", builder => builder.Expire(TimeSpan.FromSeconds(10)));
            options.AddPolicy("Medium", builder => builder.Expire(TimeSpan.FromSeconds(30)));
            options.AddPolicy("Long", builder => builder.Expire(TimeSpan.FromMinutes(5)));
        });

        return services;
    }
}
