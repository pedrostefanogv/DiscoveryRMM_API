using Discovery.Api.Filters;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services.ObjectStorage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using Discovery.Api;

namespace Discovery.Api.Controllers;

/// <summary>
/// Telemetria do object storage S3-compativel para o dashboard.
///
/// Regra central: o endpoint apenas LE cache. O probe ativo (HEAD bucket) e
/// executado pelo ObjectStorageHealthService no maximo uma vez por janela de
/// cache, com single-flight e circuit breaker. Nenhuma listagem/varredura e
/// disparada por request, inclusive em S3 externo.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/ops/object-storage")]
public class OpsObjectStorageController(
    ObjectStorageHealthService healthService,
    ObjectStorageTelemetry telemetry,
    IConfigurationResolver configurationResolver,
    IOptions<ObjectStorageOptions> objectStorageOptions,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet("telemetry")]
    [RequirePermission(ResourceType.Dashboard, ActionType.View)]
    public async Task<IActionResult> GetTelemetry(CancellationToken ct)
    {
        var server = await configurationResolver.GetServerAsync();
        var options = objectStorageOptions.Value;
        var internalMode = options.IsInternal || server.ObjectStorageInternal;

        var settings = BuildSettings(server, options, internalMode);
        var configured = settings.IsConfigured();
        var usageScanEnabled = configuration.GetValue("ObjectStorage:Telemetry:EnableUsageScan", false);

        ObjectStorageHealthSnapshot? health = null;
        if (configured)
        {
            // Le do cache; com cache quente NAO gera I/O para o S3.
            health = await healthService.GetAsync(ct);
        }

        var maxAgeSeconds = health is null
            ? 60
            : Math.Max(30, (int)Math.Ceiling((health.NextCheckAfterUtc - DateTime.UtcNow).TotalSeconds));
        Response.Headers.CacheControl = $"private, max-age={maxAgeSeconds}";

        var traffic = telemetry.Snapshot();

        return Ok(new ObjectStorageTelemetryDto(
            Configured: configured,
            Mode: configured ? (internalMode ? "internal" : "external") : "not_configured",
            Provider: "S3Compatible",
            EndpointHost: HostOf(settings.Endpoint),
            Bucket: string.IsNullOrWhiteSpace(settings.BucketName) ? null : settings.BucketName,
            Region: string.IsNullOrWhiteSpace(settings.Region) ? null : settings.Region,
            UsePathStyle: settings.UsePathStyle,
            SslVerify: settings.SslVerify,
            PresignTtlHours: settings.UrlTtlHours,
            Reachable: health?.Reachable,
            BucketReachable: health?.BucketReachable,
            LatencyMs: health?.LatencyMs,
            ConfigurationValid: health?.ConfigurationValid ?? false,
            CheckedAtUtc: health?.CheckedAtUtc,
            NextCheckAfterUtc: health?.NextCheckAfterUtc,
            Throttled: health?.Throttled ?? false,
            ConsecutiveFailures: health?.ConsecutiveFailures ?? 0,
            Errors: health?.Errors ?? [],
            UsageScanEnabled: usageScanEnabled,
            Traffic: new ObjectStorageTrafficDto(
                SinceUtc: traffic.SinceUtc,
                Uploads: traffic.Uploads,
                Downloads: traffic.Downloads,
                Presigns: traffic.Presigns,
                Heads: traffic.Heads,
                Deletes: traffic.Deletes,
                BytesIn: traffic.BytesIn,
                BytesOut: traffic.BytesOut,
                Operations: traffic.Operations,
                Failures: traffic.Failures,
                ErrorRate: traffic.ErrorRate,
                P95LatencyMs: traffic.P95LatencyMs,
                MaxLatencyMs: traffic.MaxLatencyMs,
                InFlight: traffic.InFlight,
                LastSuccessAtUtc: traffic.LastSuccessAtUtc,
                LastFailureAtUtc: traffic.LastFailureAtUtc,
                LastError: traffic.LastError),
            GeneratedAtUtc: DateTime.UtcNow));
    }

    /// <summary>Espelha a resolucao do ObjectStorageProviderFactory (interno vs banco).</summary>
    private static ObjectStorageSettings BuildSettings(
        ServerConfiguration server,
        ObjectStorageOptions options,
        bool internalMode)
    {
        if (!internalMode)
        {
            return new ObjectStorageSettings
            {
                BucketName = server.ObjectStorageBucketName,
                Endpoint = server.ObjectStorageEndpoint,
                Region = server.ObjectStorageRegion,
                AccessKey = server.ObjectStorageAccessKey,
                SecretKey = server.ObjectStorageSecretKey,
                UrlTtlHours = server.ObjectStorageUrlTtlHours > 0 ? server.ObjectStorageUrlTtlHours : 24,
                UsePathStyle = server.ObjectStorageUsePathStyle,
                SslVerify = server.ObjectStorageSslVerify
            };
        }

        return new ObjectStorageSettings
        {
            BucketName = FirstNonEmpty(server.ObjectStorageBucketName, options.Bucket),
            Endpoint = FirstNonEmpty(options.InternalEndpoint, "http://127.0.0.1:9000"),
            Region = FirstNonEmpty(server.ObjectStorageRegion, options.Region, "us-east-1"),
            AccessKey = options.AccessKey,
            SecretKey = options.SecretKey,
            UrlTtlHours = server.ObjectStorageUrlTtlHours > 0
                ? server.ObjectStorageUrlTtlHours
                : (options.UrlTtlHours > 0 ? options.UrlTtlHours : 24),
            UsePathStyle = true,
            SslVerify = server.ObjectStorageSslVerify
        };
    }

    /// <summary>Extrai somente o host do endpoint (sem credenciais, path ou query).</summary>
    private static string? HostOf(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        var raw = endpoint.Trim();
        var candidate = raw.Contains("://", StringComparison.Ordinal) ? raw : "https://" + raw;

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : null;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }
}
