using System.Reflection;
using System.Text.Json;
using Discovery.Api.Controllers;
using Discovery.Core.Entities;

namespace Discovery.Tests;

public class AgentHardwareInventoryParsingTests
{
    [Test]
    public void TryBuildComponentsFromInventoryRaw_ShouldSanitizeNetworkCollections()
    {
        var agentId = Guid.NewGuid();
        var collectedAt = new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc);

        var payload = new
        {
            listeningPorts = new object[]
            {
                new { processName = "svc.exe", processId = 100, processPath = "C:/svc.exe", protocol = "tcp", address = "0.0.0.0", port = 41080 },
                new { processName = "svc.exe", processId = 100, processPath = "C:/svc.exe", protocol = "tcp", address = "0.0.0.0", port = 41080 },
                new { processName = "invalid.exe", processId = 200, processPath = "C:/invalid.exe", protocol = "tcp", address = "127.0.0.1", port = 0 }
            },
            openSockets = new object[]
            {
                new { processName = "svc.exe", processId = 100, processPath = "C:/svc.exe", localAddress = "10.0.0.1", localPort = 41080, remoteAddress = "10.0.0.2", remotePort = 52344, protocol = "tcp", family = "2" },
                new { processName = "svc.exe", processId = 100, processPath = "C:/svc.exe", localAddress = "10.0.0.1", localPort = 41080, remoteAddress = "10.0.0.2", remotePort = 52344, protocol = "tcp", family = "2" },
                new { processName = "invalid.exe", processId = 300, processPath = "C:/invalid.exe", localAddress = "0.0.0.0", localPort = 0, remoteAddress = "0.0.0.0", remotePort = 0, protocol = "udp", family = "2" }
            }
        };

        var components = InvokeTryBuildComponents(JsonSerializer.Serialize(payload), agentId, collectedAt);

        Assert.That(components, Is.Not.Null);
        Assert.That(components!.ListeningPorts.Count, Is.EqualTo(1));
        Assert.That(components.OpenSockets.Count, Is.EqualTo(1));

        var listeningPort = components.ListeningPorts[0];
        Assert.That(listeningPort.Port, Is.EqualTo(41080));
        Assert.That(listeningPort.ProcessId, Is.EqualTo(100));

        var socket = components.OpenSockets[0];
        Assert.That(socket.LocalPort, Is.EqualTo(41080));
        Assert.That(socket.RemotePort, Is.EqualTo(52344));
        Assert.That(socket.ProcessId, Is.EqualTo(100));
    }

    [Test]
    public void TryBuildComponentsFromInventoryRaw_ShouldApplyCollectionLimits()
    {
        var agentId = Guid.NewGuid();
        var collectedAt = DateTime.UtcNow;

        var listeningPorts = Enumerable.Range(1, 250)
            .Select(i => new
            {
                processName = "svc.exe",
                processId = 1000 + i,
                processPath = "C:/svc.exe",
                protocol = "tcp",
                address = "0.0.0.0",
                port = 10000 + i
            })
            .ToArray();

        var openSockets = Enumerable.Range(1, 600)
            .Select(i => new
            {
                processName = "svc.exe",
                processId = 2000 + i,
                processPath = "C:/svc.exe",
                localAddress = "10.0.0.1",
                localPort = 20000 + i,
                remoteAddress = "10.0.0.2",
                remotePort = 30000 + i,
                protocol = "tcp",
                family = "2"
            })
            .ToArray();

        var payload = new
        {
            listeningPorts,
            openSockets
        };

        var components = InvokeTryBuildComponents(JsonSerializer.Serialize(payload), agentId, collectedAt);

        Assert.That(components, Is.Not.Null);
        Assert.That(components!.ListeningPorts.Count, Is.EqualTo(200));
        Assert.That(components.OpenSockets.Count, Is.EqualTo(500));
    }

    private static AgentHardwareComponents? InvokeTryBuildComponents(string inventoryRaw, Guid agentId, DateTime collectedAt)
    {
        var method = typeof(AgentAuthController).GetMethod(
            "TryBuildComponentsFromInventoryRaw",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(method, Is.Not.Null, "Could not find private parser method.");

        return method!.Invoke(null, [inventoryRaw, agentId, collectedAt]) as AgentHardwareComponents;
    }
}
