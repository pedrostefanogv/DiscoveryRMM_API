using System.Threading.RateLimiting;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Registers partitioned rate limiting: auth, agent, and general tiers.
/// </summary>
public static class RateLimitingServiceCollectionExtensions
{
    public static IServiceCollection AddDiscoveryRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var generalPermit = Math.Max(1, configuration.GetValue<int?>("Security:RateLimiting:General:PermitLimit") ?? 240);
        var generalWindow = Math.Max(1, configuration.GetValue<int?>("Security:RateLimiting:General:WindowSeconds") ?? 60);
        var generalQueue = Math.Max(0, configuration.GetValue<int?>("Security:RateLimiting:General:QueueLimit") ?? 0);

        var authPermit = Math.Max(1, configuration.GetValue<int?>("Security:RateLimiting:Auth:PermitLimit") ?? 20);
        var authWindow = Math.Max(1, configuration.GetValue<int?>("Security:RateLimiting:Auth:WindowSeconds") ?? 60);
        var authQueue = Math.Max(0, configuration.GetValue<int?>("Security:RateLimiting:Auth:QueueLimit") ?? 0);

        var agentPermit = Math.Max(1, configuration.GetValue<int?>("Security:RateLimiting:Agent:PermitLimit") ?? 600);
        var agentWindow = Math.Max(1, configuration.GetValue<int?>("Security:RateLimiting:Agent:WindowSeconds") ?? 60);
        var agentQueue = Math.Max(0, configuration.GetValue<int?>("Security:RateLimiting:Agent:QueueLimit") ?? 0);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
            {
                if (context.HttpContext.Response.HasStarted)
                    return;

                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Too many requests. Try again later." },
                    cancellationToken: token);
            };

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                var ip = ResolveClientIp(httpContext);
                var path = httpContext.Request.Path;

                if (path.StartsWithSegments("/api/v1/auth", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/api/v1/agent-install", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/api/v1/mfa", StringComparison.OrdinalIgnoreCase))
                {
                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: $"auth:{ip}",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = authPermit,
                            Window = TimeSpan.FromSeconds(authWindow),
                            QueueLimit = authQueue,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true
                        });
                }

                if (path.StartsWithSegments("/api/v1/agent-auth", StringComparison.OrdinalIgnoreCase))
                {
                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: $"agent:{ip}",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = agentPermit,
                            Window = TimeSpan.FromSeconds(agentWindow),
                            QueueLimit = agentQueue,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true
                        });
                }

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: $"general:{ip}",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = generalPermit,
                        Window = TimeSpan.FromSeconds(generalWindow),
                        QueueLimit = generalQueue,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    });
            });
        });

        return services;
    }

    /// <summary>
    /// Resolves the client IP, respecting Cloudflare and reverse-proxy headers.
    /// </summary>
    private static string ResolveClientIp(HttpContext context)
    {
        var cfConnectingIp = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(cfConnectingIp))
            return cfConnectingIp;

        var xForwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xForwardedFor))
        {
            var firstIp = xForwardedFor.Split(',')[0].Trim();
            if (!string.IsNullOrWhiteSpace(firstIp))
                return firstIp;
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
