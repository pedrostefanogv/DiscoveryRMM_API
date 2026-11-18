using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.E2E.Tests.Harness;
using Discovery.Infrastructure.Messaging;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.NKeys;

namespace Discovery.E2E.Tests.Tier1;

[TestFixture]
public class F04_JetStreamIsolationTests
{
    private ApiE2ETestHost _host = null!;
    private E2ETenantWorld _world = null!;
    private FakeNatsTcpServer _natsServer = null!;
    private KeyPair _accountKeyPair = null!;
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
        _accountKeyPair = KeyPair.CreatePair(PrefixByte.Account);

        _host = await ApiE2ETestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Nats:Url"] = _natsServer.Url,
            ["Nats:AccountSeed"] = _accountKeyPair.GetSeed()
        });
        _world = await E2ESeedHelper.SeedMultiTenantWorldAsync(_host);

        _natsConn = new NatsConnection(new NatsOpts { Url = _natsServer.Url });
        await _natsConn.ConnectAsync();

        _messaging = new NatsAgentMessaging(
            _natsConn,
            _host.Services.GetRequiredService<IServiceScopeFactory>(),
            _host.Services.GetRequiredService<DashboardEventContractNormalizer>(),
            new StaticOptionsMonitor<NatsGlobalPongOptions>(new NatsGlobalPongOptions { PublishIntervalSeconds = 3600 }),
            NullLogger<NatsAgentMessaging>.Instance,
            new FakeNatsTenantStreamProvisioner());

        _subCts = new CancellationTokenSource();
        _subTask = Task.Run(() => _messaging.SubscribeToAgentMessagesAsync(_subCts.Token));
        await _natsServer.WaitForSubscriptionAsync("tenant.*.site.*.agent.*.heartbeat");
    }

    [TearDown]
    public async Task TearDown()
    {
        _subCts?.Cancel();
        try { await _subTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _subCts?.Dispose();
        await _messaging.DisposeAsync();
        await _natsConn.DisposeAsync();
        _accountKeyPair?.Dispose();
        await _host.DisposeAsync();
        await _natsServer.DisposeAsync();
    }

    [Test]
    public async Task T1_F04_01_Agent_Nats_Credentials_Contain_Tenant_Scoped_Fanout_And_SupportChat_Streams()
    {
        using var ephUser = KeyPair.CreatePair(PrefixByte.User);
        string jwt = string.Empty;

        await _host.WithScopeAsync(async sp =>
        {
            var creds = sp.GetRequiredService<INatsCredentialsService>();
            var issued = await creds.IssueUserJwtForAgentAsync(ephUser.GetPublicKey(), _world.AgentA1.Id);
            jwt = issued.Jwt;
        });

        var (_, _, pubAllow, _) = NatsAuthCalloutTestHelper.DecodeUserJwt(jwt, _accountKeyPair);
        var expectedFanoutStream = NatsSubjectBuilder.TenantFanoutStreamName(_world.ClientA.Id);
        var expectedChatStream = NatsSubjectBuilder.TenantSupportChatStreamName(_world.ClientA.Id);

        Assert.That(pubAllow, Does.Contain($"$JS.API.CONSUMER.CREATE.{expectedFanoutStream}.>"));
        Assert.That(pubAllow, Does.Contain($"$JS.ACK.{expectedFanoutStream}.>"));
        Assert.That(pubAllow, Does.Contain($"$JS.API.CONSUMER.CREATE.{expectedChatStream}.>"));
        Assert.That(pubAllow, Does.Contain($"$JS.ACK.{expectedChatStream}.>"));
    }

    [Test]
    public async Task T1_F04_02_Heartbeat_Subject_Overrides_Spoofed_Payload_ClientId_And_SiteId()
    {
        _host.FakeHeartbeatCache.Clear();

        var subject = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.heartbeat";
        var spoofedPayload = JsonSerializer.Serialize(new
        {
            agentId = _world.AgentB1.Id,
            clientId = _world.ClientB.Id,
            siteId = _world.SiteB1.Id,
            cpuPercent = 12.0,
            memoryPercent = 30.0,
            diskPercent = 40.0
        });

        await _natsServer.PublishAsync(subject, spoofedPayload);

        var dashMsg = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.dashboard.events");

        using var doc = JsonDocument.Parse(dashMsg.PayloadText);
        Assert.That(doc.RootElement.GetProperty("clientId").GetGuid(), Is.EqualTo(_world.ClientA.Id));
        Assert.That(doc.RootElement.GetProperty("siteId").GetGuid(), Is.EqualTo(_world.SiteA1.Id));
        Assert.That(doc.RootElement.GetProperty("data").GetProperty("agentId").GetGuid(), Is.EqualTo(_world.AgentA1.Id));
    }

    [Test]
    public async Task T1_F04_03_PeerOnline_P2pEvent_Uses_Subject_ClientId_Not_Spoofed_Payload()
    {
        _host.FakeHeartbeatCache.Clear();

        var subject = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.heartbeat";
        var spoofedPayload = JsonSerializer.Serialize(new
        {
            agentId = _world.AgentA1.Id,
            clientId = _world.ClientB.Id,
            siteId = _world.SiteB1.Id,
            peerId = "12D3KooWSpoofTest",
            cpuPercent = 12.0,
            memoryPercent = 30.0,
            diskPercent = 40.0
        });

        await _natsServer.PublishAsync(subject, spoofedPayload);

        var p2pMsg = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.p2p.events");
        using var doc = JsonDocument.Parse(p2pMsg.PayloadText);
        Assert.That(doc.RootElement.GetProperty("clientId").GetGuid(), Is.EqualTo(_world.ClientA.Id));
        Assert.That(doc.RootElement.GetProperty("siteId").GetGuid(), Is.EqualTo(_world.SiteA1.Id));
    }

    [Test]
    public async Task T1_F04_04_Site_And_Client_Fanout_Publish_To_Tenant_Scoped_Subjects()
    {
        var siteEnvelope = new CommandDispatchEnvelope
        {
            DispatchId = Guid.NewGuid(),
            CommandType = "Shell",
            TargetScope = "site",
            TargetClientId = _world.ClientA.Id,
            TargetSiteId = _world.SiteA1.Id,
            IssuedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Payload = "{}"
        };

        var clientEnvelope = siteEnvelope with
        {
            DispatchId = Guid.NewGuid(),
            TargetScope = "client",
            TargetSiteId = null,
            IdempotencyKey = Guid.NewGuid().ToString("N")
        };

        await _messaging.PublishSiteFanoutCommandAsync(_world.ClientA.Id, _world.SiteA1.Id, siteEnvelope);
        await _messaging.PublishClientFanoutCommandAsync(_world.ClientA.Id, clientEnvelope);

        var siteMsg = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agents.command");
        var clientMsg = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.agents.command");

        Assert.That(siteMsg.Headers.ContainsKey("Nats-Msg-Id"), Is.True);
        Assert.That(clientMsg.Headers.ContainsKey("Nats-Msg-Id"), Is.True);
    }

    [Test]
    public async Task T1_F04_05_SupportChat_Server_Publish_Targets_Exact_Tenant_Site_Agent_Subject()
    {
        var envelope = new TicketChatEnvelope
        {
            EventType = "message",
            SessionId = Guid.NewGuid(),
            TicketId = _world.TicketA1.Id,
            Body = "Hello from support"
        };

        await _messaging.PublishSupportChatServerAsync(_world.ClientA.Id, _world.SiteA1.Id, _world.AgentA1.Id, envelope);

        var chatMsg = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.supportchat.server");
        Assert.That(chatMsg.PayloadText, Does.Contain("Hello from support"));
        Assert.That(chatMsg.Headers.ContainsKey("Nats-Msg-Id"), Is.True);
    }
}
