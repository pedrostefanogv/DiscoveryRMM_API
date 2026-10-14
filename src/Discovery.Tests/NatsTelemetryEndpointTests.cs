using Discovery.Infrastructure.Services.Realtime;

namespace Discovery.Tests;

/// <summary>
/// O probe TCP do NATS nao pode gerar falso "Unreachable": aqui garantimos que
/// a URL e interpretada corretamente (inclusive portas padrao por esquema).
/// </summary>
public class NatsTelemetryEndpointTests
{
    [Test]
    public void ParseEndpoint_UsesSchemeDefaultPorts()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NatsTelemetryService.ParseEndpoint("nats://localhost"), Is.EqualTo(("localhost", 4222)));
            Assert.That(NatsTelemetryService.ParseEndpoint("nats://nats.internal:4333"), Is.EqualTo(("nats.internal", 4333)));
            Assert.That(NatsTelemetryService.ParseEndpoint("tls://nats.example.com"), Is.EqualTo(("nats.example.com", 4222)));
            Assert.That(NatsTelemetryService.ParseEndpoint("ws://nats.example.com"), Is.EqualTo(("nats.example.com", 80)));
            Assert.That(NatsTelemetryService.ParseEndpoint("wss://nats.example.com"), Is.EqualTo(("nats.example.com", 443)));
        });
    }

    [Test]
    public void ParseEndpoint_InvalidInput_ReturnsNull()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NatsTelemetryService.ParseEndpoint(null), Is.Null);
            Assert.That(NatsTelemetryService.ParseEndpoint("   "), Is.Null);
            Assert.That(NatsTelemetryService.ParseEndpoint("not a url"), Is.Null);
        });
    }
}
