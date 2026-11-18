using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.E2E.Tests.Harness;
using Discovery.Infrastructure.Messaging;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Discovery.E2E.Tests.Tier1;

[TestFixture]
public class F03_NatsAgentMessagingTests
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

        var scopeFactory = _host.Services.GetRequiredService<IServiceScopeFactory>();
        var normalizer = _host.Services.GetRequiredService<DashboardEventContractNormalizer>();
        var pongOpts = new StaticOptionsMonitor<NatsGlobalPongOptions>(new NatsGlobalPongOptions
        {
            PublishIntervalSeconds = 3600
        });

        _messaging = new NatsAgentMessaging(
            _natsConn,
            scopeFactory,
            normalizer,
            pongOpts,
            NullLogger<NatsAgentMessaging>.Instance);

        _subCts = new CancellationTokenSource();
        _subTask = Task.Run(() => _messaging.SubscribeToAgentMessagesAsync(_subCts.Token));
        await _natsServer.WaitForSubscriptionAsync("tenant.*.site.*.agent.*.heartbeat");
        await _natsServer.WaitForSubscriptionAsync("tenant.*.site.*.agent.*.result");
        await _natsServer.WaitForSubscriptionAsync("tenant.*.site.*.agent.*.hardware");
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
    public async Task T1_F03_01_Transition_Heartbeat_Updates_Cache_And_Publishes_Dashboard_And_PeerOnline()
    {
        _host.FakeHeartbeatCache.Clear();

        var subject = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.heartbeat";
        var payload = JsonSerializer.Serialize(new
        {
            agentId = _world.AgentA1.Id,
            clientId = _world.ClientA.Id,
            siteId = _world.SiteA1.Id,
            ipAddress = "10.10.1.5",
            hostname = "WIN-ALPHA-1",
            agentVersion = "2.0.0",
            timestampUtc = DateTime.UtcNow,
            cpuPercent = 22.5,
            memoryPercent = 45.0,
            diskPercent = 60.0,
            peerId = "12D3KooWTestPeerAlpha",
            addrs = new[] { "/ip4/10.10.1.5/tcp/4001" },
            port = 4001
        });

        await _natsServer.PublishAsync(subject, payload);

        var dashMsg = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.dashboard.events");
        Assert.That(dashMsg.PayloadText, Does.Contain("AgentHeartbeat"));

        var p2pMsg = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.p2p.events");
        Assert.That(p2pMsg.PayloadText, Does.Contain("peer.online"));
        Assert.That(await _host.FakeHeartbeatCache.IsOnlineAsync(_world.AgentA1.Id), Is.True);
    }

    [Test]
    public async Task T1_F03_02_SteadyState_Heartbeat_Skips_PeerOnline_And_Publishes_Dashboard_Event()
    {
        _host.FakeHeartbeatCache.SeedOnline(_world.AgentA1.Id);
        _natsServer.ClearPublished();

        var subject = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.heartbeat";
        var payload = JsonSerializer.Serialize(new
        {
            agentId = _world.AgentA1.Id,
            clientId = _world.ClientA.Id,
            siteId = _world.SiteA1.Id,
            ipAddress = "10.10.1.5",
            hostname = "WIN-ALPHA-1",
            timestampUtc = DateTime.UtcNow,
            cpuPercent = 15.0,
            memoryPercent = 30.0,
            diskPercent = 50.0,
            peerId = "12D3KooWTestPeerAlpha"
        });

        await _natsServer.PublishAsync(subject, payload);

        var dashMsg = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.dashboard.events");
        Assert.That(dashMsg.PayloadText, Does.Contain("AgentHeartbeat"));
        Assert.That(_natsServer.PublishedMessages.Any(m => m.Subject.EndsWith(".p2p.events", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task T1_F03_03_Command_Result_Message_Updates_AgentCommand_Status_In_Db()
    {
        var cmdId = Guid.NewGuid();
        await _host.WithDbAsync(async db =>
        {
            db.AgentCommands.Add(new AgentCommand
            {
                Id = cmdId,
                AgentId = _world.AgentA1.Id,
                CommandType = CommandType.Shell,
                Payload = "{}",
                Status = CommandStatus.Sent,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        });

        var subject = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.result";
        var payload = JsonSerializer.Serialize(new
        {
            commandId = cmdId,
            exitCode = 0,
            output = "hostname-ok",
            errorMessage = (string?)null
        });

        await _natsServer.PublishAsync(subject, payload);

        var dashMsg = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.dashboard.events"
                 && m.PayloadText.Contains("CommandCompleted", StringComparison.Ordinal));
        Assert.That(dashMsg.PayloadText, Does.Contain(cmdId.ToString()));

        var status = await _host.WithDbAsync(async db =>
            (await db.AgentCommands.AsNoTracking().FirstAsync(c => c.Id == cmdId)).Status);
        Assert.That(status, Is.EqualTo(CommandStatus.Completed));
    }

    [Test]
    public async Task T1_F03_04_Hardware_Report_Persists_AgentHardwareInfo_And_Supports_AsNoTracking_Reads()
    {
        var subject = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.hardware";
        var payload = JsonSerializer.Serialize(new
        {
            osVersion = "Windows 11 Pro 24H2",
            machineScore = 92,
            hardware = new
            {
                manufacturer = "Dell",
                model = "OptiPlex 7010",
                processor = "Intel Core i7-13700",
                processorCores = 16,
                totalMemoryBytes = 34359738368L
            }
        });

        await _natsServer.PublishAsync(subject, payload);

        var dashMsg = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.dashboard.events"
                 && m.PayloadText.Contains("AgentHardwareReported", StringComparison.Ordinal));
        Assert.That(dashMsg.PayloadText, Does.Contain(_world.AgentA1.Id.ToString()));

        await _host.WithScopeAsync(async sp =>
        {
            var hwRepo = sp.GetRequiredService<IAgentHardwareRepository>();
            var hw = await hwRepo.GetByAgentIdAsync(_world.AgentA1.Id);
            Assert.That(hw, Is.Not.Null);
            Assert.That(hw!.Processor, Is.EqualTo("Intel Core i7-13700"));
        });
    }

    [Test]
    public async Task T1_F03_05_Concurrent_Heartbeats_And_Results_Complete_Without_DbContext_Conflict()
    {
        var cmdIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        await _host.WithDbAsync(async db =>
        {
            foreach (var id in cmdIds)
            {
                db.AgentCommands.Add(new AgentCommand
                {
                    Id = id,
                    AgentId = _world.AgentA1.Id,
                    CommandType = CommandType.Shell,
                    Payload = "{}",
                    Status = CommandStatus.Sent,
                    CreatedAt = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync();
        });

        var tasks = new List<Task>();
        for (var i = 0; i < cmdIds.Length; i++)
        {
            var idx = i;
            tasks.Add(_natsServer.PublishAsync(
                $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.heartbeat",
                JsonSerializer.Serialize(new { agentId = _world.AgentA1.Id, cpuPercent = 10 + idx, memoryPercent = 40.0, diskPercent = 50.0 })));

            tasks.Add(_natsServer.PublishAsync(
                $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.result",
                JsonSerializer.Serialize(new { commandId = cmdIds[idx], exitCode = 0, output = $"ok-{idx}" })));
        }

        await Task.WhenAll(tasks);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var doneCount = await _host.WithDbAsync(async db =>
                await db.AgentCommands.AsNoTracking().CountAsync(c => cmdIds.Contains(c.Id) && c.Status == CommandStatus.Completed));
            if (doneCount == cmdIds.Length)
                break;
            await Task.Delay(50);
        }

        var finalCount = await _host.WithDbAsync(async db =>
            await db.AgentCommands.AsNoTracking().CountAsync(c => cmdIds.Contains(c.Id) && c.Status == CommandStatus.Completed));
        Assert.That(finalCount, Is.EqualTo(cmdIds.Length));
    }
}
