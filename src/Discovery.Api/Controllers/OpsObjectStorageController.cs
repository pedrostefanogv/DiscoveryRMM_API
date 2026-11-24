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
    IObjectStorageProviderFactory storageFactory,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet("telemetry")]
    [RequirePermission(ResourceType.Dashboard, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> GetTelemetry(CancellationToken ct)
    {
        var effective = await storageFactory.ResolveEffectiveSettingsAsync(ct);

        ObjectStorageHealthSnapshot? health = null;
        if (effective.Configured)
        {
            // Le do cache; com cache quente NAO gera I/O para o S3.
            health = await healthService.GetAsync(ct);
        }

        var maxAgeSeconds = health is null
            ? 60
            : Math.Max(30, (int)Math.Ceiling((health.NextCheckAfterUtc - DateTime.UtcNow).TotalSeconds));
        Response.Headers.CacheControl = $"private, max-age={maxAgeSeconds}";

        return Ok(BuildDto(effective, health));
    }

    [HttpPost("probe")]
    [RequirePermission(ResourceType.Dashboard, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> ProbeTelemetry(CancellationToken ct)
    {
        var effective = await storageFactory.ResolveEffectiveSettingsAsync(ct);
        var health = effective.Configured
            ? await healthService.RefreshAsync(ct)
            : null;

        Response.Headers.CacheControl = "no-store";
        return Ok(BuildDto(effective, health));
    }

    private ObjectStorageTelemetryDto BuildDto(
        ObjectStorageEffectiveSettings effective,
        ObjectStorageHealthSnapshot? health)
    {
        var settings = effective.Settings;
        var usageScanEnabled = configuration.GetValue("ObjectStorage:Telemetry:EnableUsageScan", false);
        var traffic = telemetry.Snapshot();

        return new ObjectStorageTelemetryDto(
            Configured: effective.Configured,
            Mode: effective.Configured ? (effective.InternalMode ? "internal" : "external") : "not_configured",
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
            GeneratedAtUtc: DateTime.UtcNow);
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
}
