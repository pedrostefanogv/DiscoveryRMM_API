using Discovery.Api.DependencyInjection;
using OpenTelemetry.Exporter;

namespace Discovery.Tests;

public class OpenTelemetryOptionsTests
{
    [TestCase("grpc", OtlpExportProtocol.Grpc)]
    [TestCase("http", OtlpExportProtocol.HttpProtobuf)]
    [TestCase("http/protobuf", OtlpExportProtocol.HttpProtobuf)]
    [TestCase("httpprotobuf", OtlpExportProtocol.HttpProtobuf)]
    [TestCase("invalid", OtlpExportProtocol.Grpc)]
    public void GetOtlpProtocol_ShouldNormalizeSupportedAliases(string protocol, OtlpExportProtocol expected)
    {
        var options = new DiscoveryOpenTelemetryOptions
        {
            Protocol = protocol
        };

        options.Normalize("discovery-api", "1.0.0");

        Assert.That(options.GetOtlpProtocol(), Is.EqualTo(expected));
    }

    [Test]
    public void Normalize_ShouldApplySafeDefaults()
    {
        var options = new DiscoveryOpenTelemetryOptions
        {
            ServiceName = " ",
            ServiceVersion = " ",
            OtlpEndpoint = " ",
            Headers = " ",
            Protocol = "invalid",
            MetricExportIntervalMilliseconds = 0
        };

        options.Normalize("discovery-api", "9.9.9");

        Assert.Multiple(() =>
        {
            Assert.That(options.ServiceName, Is.EqualTo("discovery-api"));
            Assert.That(options.ServiceVersion, Is.EqualTo("9.9.9"));
            Assert.That(options.OtlpEndpoint, Is.Null);
            Assert.That(options.Headers, Is.Null);
            Assert.That(options.Protocol, Is.EqualTo("grpc"));
            Assert.That(options.MetricExportIntervalMilliseconds, Is.EqualTo(10000));
        });
    }
}