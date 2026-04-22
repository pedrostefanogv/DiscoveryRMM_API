using System.Reflection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Discovery.Api.DependencyInjection;

public static class OpenTelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddDiscoveryOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration.GetSection(DiscoveryOpenTelemetryOptions.SectionName).Get<DiscoveryOpenTelemetryOptions>()
            ?? new DiscoveryOpenTelemetryOptions();

        var defaultServiceVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        options.Normalize(environment.ApplicationName, defaultServiceVersion);

        if (!options.ShouldEnable())
            return services;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: options.ServiceName!,
                    serviceVersion: options.ServiceVersion,
                    serviceInstanceId: Environment.MachineName)
                .AddAttributes([
                    new KeyValuePair<string, object>("deployment.environment", environment.EnvironmentName)
                ]))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation(instrumentation =>
                    instrumentation.RecordException = true);

                if (options.EnableHttpClientInstrumentation)
                {
                    tracing.AddHttpClientInstrumentation(instrumentation =>
                        instrumentation.RecordException = true);
                }

                tracing.AddOtlpExporter(exporter => ConfigureOtlpExporter(exporter, options));
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation();
                metrics.AddMeter("Discovery.AutoTicket");

                if (options.EnableHttpClientInstrumentation)
                    metrics.AddHttpClientInstrumentation();

                if (options.EnableRuntimeInstrumentation)
                    metrics.AddRuntimeInstrumentation();

                metrics.AddOtlpExporter((exporter, reader) =>
                {
                    ConfigureOtlpExporter(exporter, options);
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = options.MetricExportIntervalMilliseconds;
                });
            });

        return services;
    }

    private static void ConfigureOtlpExporter(OtlpExporterOptions exporter, DiscoveryOpenTelemetryOptions options)
    {
        exporter.Protocol = options.GetOtlpProtocol();

        if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
            exporter.Endpoint = new Uri(options.OtlpEndpoint, UriKind.Absolute);

        if (!string.IsNullOrWhiteSpace(options.Headers))
            exporter.Headers = options.Headers;
    }
}