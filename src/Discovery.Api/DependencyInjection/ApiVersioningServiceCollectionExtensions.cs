using Asp.Versioning;
using Discovery.Api.Controllers;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Registers API versioning with URL path segment strategy (/api/v1/, /api/v2/).
/// v1 is the default when no version is specified for backward compatibility.
/// </summary>
public static class ApiVersioningServiceCollectionExtensions
{
    public static IServiceCollection AddDiscoveryApiVersioning(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        })
        .AddMvc()
        .AddApiExplorer(options =>
        {
            // GroupNameFormat 'v'VVV produces "v1", "v2", etc. matching OpenAPI doc names
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
            // Default API version for doc generation when none specified
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
        });

        return services;
    }
}
