using System.Text.Json;
using Discovery.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

[TestFixture]
public class DashboardEventContractNormalizerTests
{
    [Test]
    public void TransitionMode_ShouldNormalizeLegacyEnvelopeAndAliases()
    {
        var normalizer = CreateNormalizer(mode: "transition");
        var agentId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        var payload = JsonSerializer.Serialize(new
        {
            eventType = "agent_connected",
            timestamp = "2026-05-05T10:00:00Z",
            data = new
            {
                id = agentId,
                clientId,
                siteId,
                hostName = "edge-host",
                transport = "nats"
            }
        });

        var ok = normalizer.TryNormalize(payload, "nats", out var normalized);

        Assert.That(ok, Is.True);
        Assert.That(normalized, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(normalized!.EventType, Is.EqualTo("AgentConnected"));
            Assert.That(normalized.ClientId, Is.EqualTo(clientId));
            Assert.That(normalized.SiteId, Is.EqualTo(siteId));
        });

        var data = normalized!.Data!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(data.GetProperty("agentId").GetString(), Is.EqualTo(agentId.ToString()));
            Assert.That(data.TryGetProperty("id", out _), Is.False);
            Assert.That(data.GetProperty("hostname").GetString(), Is.EqualTo("edge-host"));
            Assert.That(data.TryGetProperty("hostName", out _), Is.False);
        });
    }

    [Test]
    public void StrictMode_ShouldRejectLegacyEventTypeAndTimestamp()
    {
        var normalizer = CreateNormalizer(mode: "strict");

        var payload = JsonSerializer.Serialize(new
        {
            eventType = "agent_connected",
            timestamp = "2026-05-05T10:00:00Z",
            data = new
            {
                agentId = Guid.NewGuid(),
                transport = "nats"
            }
        });

        var ok = normalizer.TryNormalize(payload, "nats", out var normalized);

        Assert.That(ok, Is.False);
        Assert.That(normalized, Is.Null);
    }

    [Test]
    public void ShouldRejectCommandCompletedWithoutCommandId()
    {
        var normalizer = CreateNormalizer(mode: "transition");

        var payload = JsonSerializer.Serialize(new
        {
            eventType = "CommandCompleted",
            timestampUtc = "2026-05-05T10:00:00Z",
            data = new
            {
                exitCode = 0,
                output = "ok"
            }
        });

        var ok = normalizer.TryNormalize(payload, "nats", out var normalized);

        Assert.That(ok, Is.False);
        Assert.That(normalized, Is.Null);
    }

    [Test]
    public void ShouldAcceptAgentConnectedWithoutTransport()
    {
        var normalizer = CreateNormalizer(mode: "strict");

        var payload = JsonSerializer.Serialize(new
        {
            eventType = "AgentConnected",
            timestampUtc = "2026-05-05T10:00:00Z",
            data = new
            {
                agentId = Guid.NewGuid()
            }
        });

        var ok = normalizer.TryNormalize(payload, "nats", out var normalized);

        Assert.That(ok, Is.True);
        Assert.That(normalized, Is.Not.Null);
    }

    private static DashboardEventContractNormalizer CreateNormalizer(string mode)
    {
        var options = Options.Create(new RealtimeContractOptions
        {
            Mode = mode,
            HardeningDateUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        return new DashboardEventContractNormalizer(
            options,
            TimeProvider.System,
            NullLogger<DashboardEventContractNormalizer>.Instance);
    }
}