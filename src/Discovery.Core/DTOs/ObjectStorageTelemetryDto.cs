namespace Discovery.Core.DTOs;

/// <summary>
/// Metricas PASSIVAS de trafego do object storage: contadores das operacoes que
/// a aplicacao ja executou. Nao representam nenhuma chamada extra ao S3.
/// </summary>
public sealed record ObjectStorageTrafficDto(
    DateTime SinceUtc,
    long Uploads,
    long Downloads,
    long Presigns,
    long Heads,
    long Deletes,
    long BytesIn,
    long BytesOut,
    long Operations,
    long Failures,
    double ErrorRate,
    double P95LatencyMs,
    double MaxLatencyMs,
    int InFlight,
    DateTime? LastSuccessAtUtc,
    DateTime? LastFailureAtUtc,
    string? LastError);

/// <summary>
/// Telemetria de object storage para o dashboard.
///
/// Nunca carrega credenciais. O bloco ativo (Reachable/LatencyMs/CheckedAtUtc)
/// vem de um snapshot cacheado com circuit breaker — a API do S3 nao e chamada
/// a cada request/refresh. Campos anulaveis significam "nao verificado".
/// Mode = "internal" | "external" | "not_configured".
/// </summary>
public sealed record ObjectStorageTelemetryDto(
    bool Configured,
    string Mode,
    string Provider,
    string? EndpointHost,
    string? Bucket,
    string? Region,
    bool UsePathStyle,
    bool SslVerify,
    int PresignTtlHours,
    bool? Reachable,
    bool? BucketReachable,
    int? LatencyMs,
    bool ConfigurationValid,
    DateTime? CheckedAtUtc,
    DateTime? NextCheckAfterUtc,
    bool Throttled,
    int ConsecutiveFailures,
    string[] Errors,
    bool UsageScanEnabled,
    ObjectStorageTrafficDto Traffic,
    DateTime GeneratedAtUtc);
