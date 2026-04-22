using OpenTelemetry.Exporter;

namespace Discovery.Api.DependencyInjection;

public sealed class DiscoveryOpenTelemetryOptions
{
    public const string SectionName = "OpenTelemetry";

    public bool Enabled { get; set; }
    public string? ServiceName { get; set; }
    public string? ServiceVersion { get; set; }
    public string? OtlpEndpoint { get; set; }
    public string Protocol { get; set; } = "grpc";
    public string? Headers { get; set; }
    public int MetricExportIntervalMilliseconds { get; set; } = 10000;
    public bool EnableHttpClientInstrumentation { get; set; } = true;
    public bool EnableRuntimeInstrumentation { get; set; } = true;

    public void Normalize(string defaultServiceName, string defaultServiceVersion)
    {
        ServiceName = string.IsNullOrWhiteSpace(ServiceName) ? defaultServiceName : ServiceName.Trim();
        ServiceVersion = string.IsNullOrWhiteSpace(ServiceVersion) ? defaultServiceVersion : ServiceVersion.Trim();
        OtlpEndpoint = string.IsNullOrWhiteSpace(OtlpEndpoint) ? null : OtlpEndpoint.Trim();
        Headers = string.IsNullOrWhiteSpace(Headers) ? null : Headers.Trim();
        MetricExportIntervalMilliseconds = MetricExportIntervalMilliseconds <= 0 ? 10000 : MetricExportIntervalMilliseconds;
        Protocol = NormalizeProtocol(Protocol);
    }

    public bool ShouldEnable()
        => Enabled
            || !string.IsNullOrWhiteSpace(OtlpEndpoint)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

    public OtlpExportProtocol GetOtlpProtocol()
        => string.Equals(Protocol, "http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;

    public static string NormalizeProtocol(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
            return "grpc";

        var normalized = protocol.Trim().ToLowerInvariant();
        return normalized switch
        {
            "http" => "http/protobuf",
            "httpprotobuf" => "http/protobuf",
            "http/protobuf" => "http/protobuf",
            _ => "grpc"
        };
    }
}