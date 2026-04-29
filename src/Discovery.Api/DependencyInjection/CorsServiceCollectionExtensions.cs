using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Registers CORS policies for the API and SignalR hubs.
/// </summary>
public static class CorsServiceCollectionExtensions
{
    public static IServiceCollection AddDiscoveryCors(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        var allowedOrigins = configuration.GetSection("Security:Cors:AllowedOrigins").Get<string[]>() ?? [];
        var allowWildcardCorsInDevelopment = configuration.GetValue("Security:Cors:AllowWildcardInDevelopment", true);

        if (!isDevelopment && allowedOrigins.Length == 0)
        {
            Console.WriteLine("WARNING: Security:Cors:AllowedOrigins is empty in non-development environment. Cross-origin requests will be blocked.");
        }

        services.AddCors(options =>
        {
            options.AddPolicy("DefaultApi", policy =>
                ApplyApiCorsPolicy(policy, isDevelopment, allowWildcardCorsInDevelopment, allowedOrigins));

            options.AddPolicy("SignalR", policy =>
                ApplySignalRCorsPolicy(policy, isDevelopment, allowWildcardCorsInDevelopment, allowedOrigins));
        });

        return services;
    }

    private static void ApplyApiCorsPolicy(
        CorsPolicyBuilder policy,
        bool isDevelopment,
        bool allowWildcardCorsInDevelopment,
        string[] allowedOrigins)
    {
        if (isDevelopment && allowWildcardCorsInDevelopment)
        {
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
            return;
        }

        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader();
        }
    }

    private static void ApplySignalRCorsPolicy(
        CorsPolicyBuilder policy,
        bool isDevelopment,
        bool allowWildcardCorsInDevelopment,
        string[] allowedOrigins)
    {
        if (isDevelopment && allowWildcardCorsInDevelopment)
        {
            policy.AllowAnyMethod().AllowAnyHeader().SetIsOriginAllowed(_ => true).AllowCredentials();
            return;
        }

        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader().AllowCredentials();
        }
    }
}
