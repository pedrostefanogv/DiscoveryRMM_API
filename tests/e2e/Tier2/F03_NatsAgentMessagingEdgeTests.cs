using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.E2E.Tests.Harness;
using Discovery.Infrastructure.Messaging;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Discovery.E2E.Tests.Tier2;

[TestFixture]
public class F03_NatsAgentMessagingEdgeTests
{
    private ApiE2ETestHost _host = null!;
    private E2ETenantWorld _world = null!;
    private FakeNatsTcpServer _natsServer = null!;
    private NatsConnection _natsConn = null!;
    private NatsAgentMessaging _messaging = null!;
    private CancellationTokenSource _subCts = null!;
    private Task _subTask = Task.CompletedTask;

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    [SetUp]
    public async Task SetUp()
    {
        _natsServer = new FakeNatsTcpServer();
        _host = await ApiE2ETestHost.StartAsync();
        _world = await E2ESeedHelper.SeedMultiTenantWorldAsync(_host);

        _natsConn = new NatsConnection(new NatsOpts { Url = _natsServer.Url });
        await _natsConn.ConnectAsync();

        _messaging = new NatsAgentMessaging(
            _natsConn,
            _host.Services.GetRequiredService<IServiceScopeFactory>(),
            _host.Services.GetRequiredService<DashboardEventContractNormalizer>(),
            new StaticOptionsMonitor<NatsGlobalPongOptions>(new NatsGlobalPongOptions { PublishIntervalSeconds = 3600 }),
            NullLogger<NatsAgentMessaging>.Instance);

        _subCts = new CancellationTokenSource();
        _subTask = Task.Run(() => _messaging.SubscribeToAgentMessagesAsync(_subCts.Token));
        await _natsServer.WaitForSubscriptionAsync("tenant.*.site.*.agent.*.heartbeat");
        await _natsServer.WaitForSubscriptionAsync("tenant.*.site.*.agent.*.result");
    }

    [TearDown]
    public async Task TearDown()
    {
        _subCts?.Cancel();
        try { await _subTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _subCts?.Dispose();
        await _messaging.DisposeAsync();
        await _natsConn.DisposeAsync();
        await _host.DisposeAsync();
        await _natsServer.DisposeAsync();
    }

    [Test]
    public async Task T2_F03_01_Trashed_Agent_Transition_Heartbeat_Suppresses_PeerOnline_Publish()
    {
        var (trashedAgent, _) = await _host.WithDbAsync(async db =>
            await E2ESeedHelper.CreateAgentWithTokenAsync(db, _world.SiteA1.Id, "TRASHED-A2", deletedAt: DateTime.UtcNow));

        _host.FakeHeartbeatCache.Clear();
        _natsServer.ClearPublished();

        var subject = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{trashedAgent.Id}.heartbeat";
        await _natsServer.PublishAsync(subject, JsonSerializer.Serialize(new
        {
            agentId = trashedAgent.Id,
            peerId = "12D3KooWTrashedPeer",
            cpuPercent = 10.0,
            memoryPercent = 20.0,
            diskPercent = 30.0
        }));

        await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.dashboard.events");

        Assert.That(_natsServer.PublishedMessages.Any(m => m.Subject.EndsWith(".p2p.events", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task T2_F03_02_Malformed_Json_Heartbeat_Does_Not_Crash_Subscription_Loop()
    {
        var badSubject = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.heartbeat";
        await _natsServer.PublishAsync(badSubject, "{not-valid-json");

        await _natsServer.PublishAsync(badSubject, JsonSerializer.Serialize(new
        {
            agentId = _world.AgentA1.Id,
            cpuPercent = 20.0,
            memoryPercent = 30.0,
            diskPercent = 40.0
        }));

        var dashMsg = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.dashboard.events");
        Assert.That(dashMsg.PayloadText, Does.Contain("AgentHeartbeat"));
    }

    [Test]
    public async Task T2_F03_03_Unknown_AgentId_Heartbeat_Is_Rejected_Without_Poisoning_Loop()
    {
        var unknownAgentId = Guid.NewGuid();
        var unknownSubject = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{unknownAgentId}.heartbeat";
        await _natsServer.PublishAsync(unknownSubject, JsonSerializer.Serialize(new
        {
            agentId = unknownAgentId,
            cpuPercent = 10.0
        }));

        var validSubject = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.heartbeat";
        await _natsServer.PublishAsync(validSubject, JsonSerializer.Serialize(new
        {
            agentId = _world.AgentA1.Id,
            cpuPercent = 33.0
        }));

        var dashMsg = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.dashboard.events");
        Assert.That(dashMsg.PayloadText, Does.Contain(_world.AgentA1.Id.ToString()));
    }

    [Test]
    public async Task T2_F03_04_NonExistent_CommandId_Result_Does_Not_Break_Result_Subscription()
    {
        var resultSubject = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.result";
        await _natsServer.PublishAsync(resultSubject, JsonSerializer.Serialize(new
        {
            commandId = Guid.NewGuid(),
            exitCode = 1,
            errorMessage = "unknown command"
        }));

        // Verify loop remains healthy
        var hbSubject = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.heartbeat";
        await _natsServer.PublishAsync(hbSubject, JsonSerializer.Serialize(new { agentId = _world.AgentA1.Id, cpuPercent = 5.0 }));

        var dashMsg = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.dashboard.events"
                 && m.PayloadText.Contains("AgentHeartbeat", StringComparison.Ordinal));
        Assert.That(dashMsg, Is.Not.Null);
    }

    [Test]
    public async Task T2_F03_05_Empty_Hardware_Payload_Does_Not_Throw_Or_Corrupt_Existing_Hardware()
    {
        var hwSubject = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.hardware";
        await _natsServer.PublishAsync(hwSubject, "{}");

        var validHb = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.heartbeat";
        await _natsServer.PublishAsync(validHb, JsonSerializer.Serialize(new { agentId = _world.AgentA1.Id, cpuPercent = 18.0 }));

        var dashMsg = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.dashboard.events");
        Assert.That(dashMsg.PayloadText, Does.Contain(_world.AgentA1.Id.ToString()));
    }
}
