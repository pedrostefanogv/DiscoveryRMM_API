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

        // Download tier: public stage2 endpoint — moderate rate for installer downloads
        var downloadPermit = Math.Max(1, configuration.GetValue<int?>("Security:RateLimiting:Download:PermitLimit") ?? 30);
        var downloadWindow = Math.Max(1, configuration.GetValue<int?>("Security:RateLimiting:Download:WindowSeconds") ?? 60);
        var downloadQueue = Math.Max(0, configuration.GetValue<int?>("Security:RateLimiting:Download:QueueLimit") ?? 5);

        // Ticket creation tier: prevent abuse
        var ticketCreatePermit = Math.Max(1, configuration.GetValue<int?>("Security:RateLimiting:TicketCreate:PermitLimit") ?? 30);
        var ticketCreateWindow = Math.Max(1, configuration.GetValue<int?>("Security:RateLimiting:TicketCreate:WindowSeconds") ?? 60);
        var ticketCreateQueue = Math.Max(0, configuration.GetValue<int?>("Security:RateLimiting:TicketCreate:QueueLimit") ?? 0);

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

                if (path.StartsWithSegments("/api/v1/download", StringComparison.OrdinalIgnoreCase))
                {
                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: $"download:{ip}",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = downloadPermit,
                            Window = TimeSpan.FromSeconds(downloadWindow),
                            QueueLimit = downloadQueue,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true
                        });
                }

                // Ticket creation: stricter rate limiting
                if (httpContext.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                    && (path.StartsWithSegments("/api/v", StringComparison.OrdinalIgnoreCase)
                        && path.Value?.Contains("/tickets", StringComparison.OrdinalIgnoreCase) == true
                        && !path.Value.Contains("/comments", StringComparison.OrdinalIgnoreCase)
                        && !path.Value.Contains("/attachments", StringComparison.OrdinalIgnoreCase)
                        && !path.Value.Contains("/audit", StringComparison.OrdinalIgnoreCase)
                        && !path.Value.Contains("/watchers", StringComparison.OrdinalIgnoreCase)
                        && !path.Value.Contains("/ai", StringComparison.OrdinalIgnoreCase)
                        && !path.Value.Contains("/sla", StringComparison.OrdinalIgnoreCase)
                        && !path.Value.Contains("/custom-fields", StringComparison.OrdinalIgnoreCase)))
                {
                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: $"tickets-create:{ip}",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = ticketCreatePermit,
                            Window = TimeSpan.FromSeconds(ticketCreateWindow),
                            QueueLimit = ticketCreateQueue,
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
    /// Resolves the client IP. After the ForwardedHeaders middleware has processed
    /// X-Forwarded-For from trusted proxies, Connection.RemoteIpAddress contains
    /// the real client IP. CF-Connecting-IP is only trusted when the connection
    /// originates from a known Cloudflare IP range (configured in Security:TrustedProxies).
    /// </summary>
    private static string ResolveClientIp(HttpContext context)
    {
        // Primary source: RemoteIpAddress already resolved by ForwardedHeaders middleware
        var remoteIp = context.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrWhiteSpace(remoteIp))
            return remoteIp;

        return "unknown";
    }
}
