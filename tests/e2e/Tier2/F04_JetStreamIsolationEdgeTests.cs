using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.E2E.Tests.Harness;
using Discovery.Infrastructure.Messaging;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.NKeys;

namespace Discovery.E2E.Tests.Tier2;

[TestFixture]
public class F04_JetStreamIsolationEdgeTests
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
    public async Task T2_F04_01_Agent_Credentials_Must_Not_Contain_Legacy_Global_Wildcard_Stream_ACLs()
    {
        using var ephUser = KeyPair.CreatePair(PrefixByte.User);
        string jwt = string.Empty;

        await _host.WithScopeAsync(async sp =>
        {
            var creds = sp.GetRequiredService<INatsCredentialsService>();
            jwt = (await creds.IssueUserJwtForAgentAsync(ephUser.GetPublicKey(), _world.AgentA1.Id)).Jwt;
        });

        var (_, _, pubAllow, subAllow) = NatsAuthCalloutTestHelper.DecodeUserJwt(jwt, _accountKeyPair);

        Assert.That(pubAllow.Any(p => p.Contains("DISCOVERY_FANOUT_COMMANDS.>", StringComparison.Ordinal)), Is.False);
        Assert.That(pubAllow.Any(p => p.Contains("DISCOVERY_SUPPORT_CHAT.>", StringComparison.Ordinal)), Is.False);
        Assert.That(subAllow.Any(s => s.Contains(_world.ClientB.Id.ToString(), StringComparison.OrdinalIgnoreCase)), Is.False);
    }

    [Test]
    public async Task T2_F04_02_AgentA_Credentials_Never_Include_TenantB_Streams_Or_Subjects()
    {
        using var ephUser = KeyPair.CreatePair(PrefixByte.User);
        string jwt = string.Empty;

        await _host.WithScopeAsync(async sp =>
        {
            var creds = sp.GetRequiredService<INatsCredentialsService>();
            jwt = (await creds.IssueUserJwtForAgentAsync(ephUser.GetPublicKey(), _world.AgentA1.Id)).Jwt;
        });

        var (_, _, pubAllow, subAllow) = NatsAuthCalloutTestHelper.DecodeUserJwt(jwt, _accountKeyPair);
        var tenantBFanout = NatsSubjectBuilder.TenantFanoutStreamName(_world.ClientB.Id);
        var tenantBChat = NatsSubjectBuilder.TenantSupportChatStreamName(_world.ClientB.Id);

        Assert.That(pubAllow.Any(p => p.Contains(tenantBFanout, StringComparison.Ordinal)), Is.False);
        Assert.That(pubAllow.Any(p => p.Contains(tenantBChat, StringComparison.Ordinal)), Is.False);
        Assert.That(pubAllow.Any(p => p.Contains(_world.ClientB.Id.ToString(), StringComparison.OrdinalIgnoreCase)), Is.False);
        Assert.That(subAllow.Any(s => s.Contains(_world.ClientB.Id.ToString(), StringComparison.OrdinalIgnoreCase)), Is.False);
    }

    [Test]
    public async Task T2_F04_03_Spoofed_Payload_ClientId_Never_Publishes_Dashboard_Event_To_TenantB()
    {
        _natsServer.ClearPublished();

        var subjectA = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.heartbeat";
        await _natsServer.PublishAsync(subjectA, JsonSerializer.Serialize(new
        {
            agentId = _world.AgentB1.Id,
            clientId = _world.ClientB.Id,
            siteId = _world.SiteB1.Id,
            cpuPercent = 15.0
        }));

        await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.dashboard.events");

        Assert.That(_natsServer.PublishedMessages.Any(m => m.Subject.Contains(_world.ClientB.Id.ToString(), StringComparison.OrdinalIgnoreCase)), Is.False);
    }

    [Test]
    public async Task T2_F04_04_NonGuid_Subject_Segments_Are_Rejected_Safely()
    {
        _natsServer.ClearPublished();
        await _natsServer.PublishAsync("tenant.not-a-guid.site.also-bad.agent.nope.heartbeat", "{\"cpuPercent\":10}");

        // Send a valid heartbeat right after to ensure loop stayed healthy without publishing for the bad subject
        var validSubject = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.heartbeat";
        await _natsServer.PublishAsync(validSubject, JsonSerializer.Serialize(new { agentId = _world.AgentA1.Id, cpuPercent = 10.0 }));

        var msg = await _natsServer.WaitForPublishedAsync(m => m.Subject.EndsWith(".dashboard.events", StringComparison.Ordinal));
        Assert.That(msg.Subject, Is.EqualTo($"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.dashboard.events"));
    }

    [Test]
    public async Task T2_F04_05_User_Scoped_Nats_Credentials_Isolate_TenantB_Subjects()
    {
        using var ephUser = KeyPair.CreatePair(PrefixByte.User);
        string jwt = string.Empty;

        await _host.WithScopeAsync(async sp =>
        {
            var creds = sp.GetRequiredService<INatsCredentialsService>();
            var scope = await sp.GetRequiredService<IPermissionService>()
                .GetScopeAccessAsync(_world.TechA.User.Id, Discovery.Core.Enums.Identity.ResourceType.Dashboard, Discovery.Core.Enums.Identity.ActionType.View);
            jwt = (await creds.IssueUserJwtForUserAsync(ephUser.GetPublicKey(), _world.TechA.User.Id, scope)).Jwt;
        });

        var (_, _, _, subAllow) = NatsAuthCalloutTestHelper.DecodeUserJwt(jwt, _accountKeyPair);
        Assert.That(subAllow.Any(s => s.Contains(_world.ClientA.Id.ToString(), StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(subAllow.Any(s => s.Contains(_world.ClientB.Id.ToString(), StringComparison.OrdinalIgnoreCase)), Is.False);
        Assert.That(subAllow, Does.Not.Contain("tenant.>"));
    }
}
