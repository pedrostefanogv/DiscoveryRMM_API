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

            // Vary por credenciais (Authorization/Cookie): vários handlers são escopados
            // por usuário (ex.: GET /clients filtra por permissões do chamador). Sem isto
            // um usuário restrito podia receber a lista global cacheada de outro usuário.
            options.AddBasePolicy(builder => builder
                .Expire(TimeSpan.FromSeconds(15))
                .SetVaryByHost(true)
                .SetVaryByHeader("Authorization", "Cookie"));

            // Named policies for different cache durations
            options.AddPolicy("Short", builder => builder
                .Expire(TimeSpan.FromSeconds(10))
                .SetVaryByHeader("Authorization", "Cookie"));
            options.AddPolicy("Medium", builder => builder
                .Expire(TimeSpan.FromSeconds(30))
                .SetVaryByHeader("Authorization", "Cookie"));
            options.AddPolicy("Long", builder => builder
                .Expire(TimeSpan.FromMinutes(5))
                .SetVaryByHeader("Authorization", "Cookie"));
        });

        return services;
    }
}
