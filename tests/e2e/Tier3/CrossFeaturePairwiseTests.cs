using System.Net;
using System.Text;
using System.Text.Json;
using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.E2E.Tests.Harness;
using Discovery.Infrastructure.Messaging;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.NKeys;

namespace Discovery.E2E.Tests.Tier3;

[TestFixture]
public class CrossFeaturePairwiseTests
{
    private ApiE2ETestHost _host = null!;
    private E2ETenantWorld _world = null!;
    private FakeNatsTcpServer _natsServer = null!;
    private KeyPair _accountKeyPair = null!;
    private KeyPair _serverKeyPair = null!;
    private NatsConnection _natsConn = null!;

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
        _serverKeyPair = KeyPair.CreatePair(PrefixByte.Server);

        _host = await ApiE2ETestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Nats:Url"] = _natsServer.Url,
            ["Nats:AccountSeed"] = _accountKeyPair.GetSeed(),
            ["Nats:ServerPublicKey"] = _serverKeyPair.GetPublicKey()
        });
        _world = await E2ESeedHelper.SeedMultiTenantWorldAsync(_host);

        _natsConn = new NatsConnection(new NatsOpts { Url = _natsServer.Url });
        await _natsConn.ConnectAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _natsConn.DisposeAsync();
        _accountKeyPair.Dispose();
        _serverKeyPair.Dispose();
        await _host.DisposeAsync();
        await _natsServer.DisposeAsync();
    }

    private NatsAuthCalloutBackgroundService CreateCalloutService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nats:Url"] = _natsServer.Url,
                ["Nats:AccountSeed"] = _accountKeyPair.GetSeed(),
                ["Nats:ServerPublicKey"] = _serverKeyPair.GetPublicKey(),
                ["Nats:AuthCallout:Enabled"] = "true",
                ["Nats:AuthCallout:Subject"] = "$SYS.REQ.USER.AUTH"
            })
            .Build();

        return new NatsAuthCalloutBackgroundService(
            _natsConn,
            config,
            _host.Services.GetRequiredService<IServiceScopeFactory>(),
            new NatsAuthCalloutReloadSignal(),
            NullLogger<NatsAuthCalloutBackgroundService>.Instance);
    }

    [Test]
    public async Task T3_F01xF04_AuthCallout_Reissuance_Preserves_Tenant_Fanout_And_Rejects_Forged_Wildcard()
    {
        using var svc = CreateCalloutService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await _natsServer.WaitForSubscriptionAsync("$SYS.REQ.USER.AUTH");

        using var origKey = KeyPair.CreatePair(PrefixByte.User);
        using var ephKey = KeyPair.CreatePair(PrefixByte.User);
        string agentJwt = string.Empty;

        await _host.WithScopeAsync(async sp =>
        {
            var creds = sp.GetRequiredService<INatsCredentialsService>();
            agentJwt = (await creds.IssueUserJwtForAgentAsync(origKey.GetPublicKey(), _world.AgentA1.Id)).Jwt;
        });

        var req = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(_serverKeyPair, ephKey.GetPublicKey(), authToken: agentJwt);
        var reply = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(req));
        var decoded = NatsAuthCalloutTestHelper.DecodeAuthResponse(reply.PayloadText, _accountKeyPair);
        Assert.That(decoded.Jwt, Is.Not.Null);

        var (_, _, pubAllow, _) = NatsAuthCalloutTestHelper.DecodeUserJwt(decoded.Jwt!, _accountKeyPair);
        Assert.That(pubAllow.Any(p => p.Contains("DISCOVERY_FANOUT_COMMANDS.>", StringComparison.Ordinal)), Is.False);
        Assert.That(pubAllow.Any(p => p.Contains($"DISCOVERY_FANOUT_COMMANDS_{_world.ClientA.Id:N}", StringComparison.Ordinal)), Is.True);

        // Tamper pre-issued JWT with global wildcard -> rejected
        var parts = agentJwt.Split('.');
        var tamperedPayload = Encoding.UTF8.GetString(NatsAuthCalloutTestHelper.Base64UrlDecode(parts[1]))
            .Replace(_world.ClientA.Id.ToString("N"), "*", StringComparison.Ordinal);
        var forged = $"{parts[0]}.{NatsAuthCalloutTestHelper.Base64UrlEncode(Encoding.UTF8.GetBytes(tamperedPayload))}.{parts[2]}";

        using var ephKey2 = KeyPair.CreatePair(PrefixByte.User);
        var forgedReq = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(_serverKeyPair, ephKey2.GetPublicKey(), authToken: forged);
        var forgedReply = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(forgedReq));
        var forgedDecoded = NatsAuthCalloutTestHelper.DecodeAuthResponse(forgedReply.PayloadText, _accountKeyPair);
        Assert.That(forgedDecoded.Jwt, Is.Null);

        await svc.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task T3_F01xF05_AuthCallout_With_Scoped_TechA_Jwt_Issues_TenantA_Only_Nats_Permissions()
    {
        using var svc = CreateCalloutService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await _natsServer.WaitForSubscriptionAsync("$SYS.REQ.USER.AUTH");

        using var ephKey = KeyPair.CreatePair(PrefixByte.User);
        var req = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(_serverKeyPair, ephKey.GetPublicKey(), authToken: _world.TechA.Jwt);
        var reply = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(req));
        var decoded = NatsAuthCalloutTestHelper.DecodeAuthResponse(reply.PayloadText, _accountKeyPair);

        Assert.That(decoded.Jwt, Is.Not.Null);
        var (_, _, _, subAllow) = NatsAuthCalloutTestHelper.DecodeUserJwt(decoded.Jwt!, _accountKeyPair);
        Assert.That(subAllow.Any(s => s.Contains(_world.ClientA.Id.ToString(), StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(subAllow.Any(s => s.Contains(_world.ClientB.Id.ToString(), StringComparison.OrdinalIgnoreCase)), Is.False);

        await svc.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task T3_F02xF05_OutputCache_With_Scoped_ApiKeys_Isolates_Cached_Clients_Per_Tenant()
    {
        using var reqA = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients")
            .WithApiKey(_world.TechA.ApiKey, _world.TechA.ApiSecret);
        using var respA = await _host.HttpClient.SendAsync(reqA);
        Assert.That(respA.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var bodyA = await respA.Content.ReadAsStringAsync();
        Assert.That(bodyA, Does.Contain(_world.ClientA.Id.ToString()));
        Assert.That(bodyA, Does.Not.Contain(_world.ClientB.Id.ToString()));

        using var reqB = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients")
            .WithApiKey(_world.TechB.ApiKey, _world.TechB.ApiSecret);
        using var respB = await _host.HttpClient.SendAsync(reqB);
        Assert.That(respB.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var bodyB = await respB.Content.ReadAsStringAsync();
        Assert.That(bodyB, Does.Contain(_world.ClientB.Id.ToString()));
        Assert.That(bodyB, Does.Not.Contain(_world.ClientA.Id.ToString()));
    }

    [Test]
    public async Task T3_F02xF07_OutputCache_Never_Caches_AgentAuth_Me_Across_Agents()
    {
        using var reqA = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me")
            .WithAgentAuth(_world.AgentTokenA1, _world.AgentA1.Id);
        using var respA = await _host.HttpClient.SendAsync(reqA);
        Assert.That(respA.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await respA.Content.ReadAsStringAsync(), Does.Contain(_world.AgentA1.Id.ToString()));

        using var reqB = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me")
            .WithAgentAuth(_world.AgentTokenB1, _world.AgentB1.Id);
        using var respB = await _host.HttpClient.SendAsync(reqB);
        Assert.That(respB.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var bodyB = await respB.Content.ReadAsStringAsync();
        Assert.That(bodyB, Does.Contain(_world.AgentB1.Id.ToString()));
        Assert.That(bodyB, Does.Not.Contain(_world.AgentA1.Id.ToString()));
    }

    [Test]
    public async Task T3_F03xF04_Real_NatsAgentMessaging_Overrides_Spoofed_Payload_Using_Subject_Authority()
    {
        await using var messaging = new NatsAgentMessaging(
            _natsConn,
            _host.Services.GetRequiredService<IServiceScopeFactory>(),
            _host.Services.GetRequiredService<DashboardEventContractNormalizer>(),
            new StaticOptionsMonitor<NatsGlobalPongOptions>(new NatsGlobalPongOptions { PublishIntervalSeconds = 3600 }),
            NullLogger<NatsAgentMessaging>.Instance);

        using var cts = new CancellationTokenSource();
        var subTask = Task.Run(() => messaging.SubscribeToAgentMessagesAsync(cts.Token));
        await _natsServer.WaitForSubscriptionAsync("tenant.*.site.*.agent.*.heartbeat");

        var subjectA = $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.heartbeat";
        await _natsServer.PublishAsync(subjectA, JsonSerializer.Serialize(new
        {
            agentId = _world.AgentB1.Id,
            clientId = _world.ClientB.Id,
            siteId = _world.SiteB1.Id,
            cpuPercent = 42.0
        }));

        var dash = await _natsServer.WaitForPublishedAsync(
            m => m.Subject == $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.dashboard.events");
        Assert.That(dash.PayloadText, Does.Contain(_world.ClientA.Id.ToString()));
        Assert.That(dash.PayloadText, Does.Not.Contain(_world.ClientB.Id.ToString()));
        cts.Cancel();
    }

    [Test]
    public async Task T3_F03xF07_Trashed_Agent_Blocked_On_Http_And_Suppressed_In_Nats_PeerOnline()
    {
        var (trashed, trashedTok) = await _host.WithDbAsync(async db =>
            await E2ESeedHelper.CreateAgentWithTokenAsync(db, _world.SiteA1.Id, "TRASHED-PAIR", deletedAt: DateTime.UtcNow));

        using var httpReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me/hardware")
            .WithAgentAuth(trashedTok, trashed.Id);
        using var httpResp = await _host.HttpClient.SendAsync(httpReq);
        Assert.That(httpResp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        await using var messaging = new NatsAgentMessaging(
            _natsConn,
            _host.Services.GetRequiredService<IServiceScopeFactory>(),
            _host.Services.GetRequiredService<DashboardEventContractNormalizer>(),
            new StaticOptionsMonitor<NatsGlobalPongOptions>(new NatsGlobalPongOptions { PublishIntervalSeconds = 3600 }),
            NullLogger<NatsAgentMessaging>.Instance);

        using var cts = new CancellationTokenSource();
        var subTask = Task.Run(() => messaging.SubscribeToAgentMessagesAsync(cts.Token));
        await _natsServer.WaitForSubscriptionAsync("tenant.*.site.*.agent.*.heartbeat");
        _natsServer.ClearPublished();

        await _natsServer.PublishAsync(
            $"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{trashed.Id}.heartbeat",
            JsonSerializer.Serialize(new { agentId = trashed.Id, peerId = "12D3KooWPairTrashed", cpuPercent = 5.0 }));

        await _natsServer.WaitForPublishedAsync(m => m.Subject.EndsWith(".dashboard.events", StringComparison.Ordinal));
        Assert.That(_natsServer.PublishedMessages.Any(m => m.Subject.EndsWith(".p2p.events", StringComparison.Ordinal)), Is.False);
        cts.Cancel();
    }

    [Test]
    public async Task T3_F05xF06_TechA_CrossTenant_Ticket_Creation_Denied_And_Transaction_Leaves_Db_Clean()
    {
        var before = await _host.WithDbAsync(async db => await db.Tickets.CountAsync());

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tickets")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                clientId = _world.ClientB.Id,
                siteId = _world.SiteB1.Id,
                title = "Cross Tenant Blocked",
                description = "Must not commit",
                priority = 1
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That((int)resp.StatusCode, Is.AnyOf(403, 404));

        var after = await _host.WithDbAsync(async db => await db.Tickets.CountAsync());
        Assert.That(after, Is.EqualTo(before));
    }

    [Test]
    public async Task T3_F05xF08_TechA_Valid_Update_Command_Allowed_For_AgentA1_Denied_For_AgentB1()
    {
        var validPayload = JsonSerializer.Serialize(new
        {
            action = "check-update"
        });

        using var reqA = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentA1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { commandType = "Update", payload = validPayload }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);
        using var respA = await _host.HttpClient.SendAsync(reqA);
        Assert.That(respA.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        using var reqB = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentB1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { commandType = "Update", payload = validPayload }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);
        using var respB = await _host.HttpClient.SendAsync(reqB);
        Assert.That((int)respB.StatusCode, Is.AnyOf(403, 404));
    }

    [Test]
    public async Task T3_F05xF09_TechA_Rdp_Allowed_On_AgentA1_And_RdpFile_Denied_On_AgentB1()
    {
        using var startReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{_world.AgentA1.Id}")
        {
            Content = new StringContent("{\"kind\":\"rdp\",\"transport\":\"nats\"}", Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);
        using var startResp = await _host.HttpClient.SendAsync(startReq);
        Assert.That((int)startResp.StatusCode, Is.InRange(200, 201));

        using var deniedReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/remote-sessions/{_world.AgentB1.Id}/{_world.RemoteSessionB1.Id}/rdp-file?localPort=33890")
            .WithJwt(_world.TechA.Jwt);
        using var deniedResp = await _host.HttpClient.SendAsync(deniedReq);
        Assert.That((int)deniedResp.StatusCode, Is.AnyOf(403, 404));
    }

    [Test]
    public async Task T3_F06xF08_Invalid_SpecialCommand_Payload_Returns_400_And_Persists_Zero_Command_Rows()
    {
        var before = await _host.WithDbAsync(async db => await db.AgentCommands.CountAsync());

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentA1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                commandType = "Update",
                payload = "{\"action\":\"force-update\"}"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var after = await _host.WithDbAsync(async db => await db.AgentCommands.CountAsync());
        Assert.That(after, Is.EqualTo(before));
    }

    [Test]
    public async Task T3_F07xF09_Agent_Mdz_AuthCallout_Grants_RemoteSession_32Hex_Wildcard_Subject()
    {
        using var svc = CreateCalloutService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await _natsServer.WaitForSubscriptionAsync("$SYS.REQ.USER.AUTH");

        using var ephKey = KeyPair.CreatePair(PrefixByte.User);
        var req = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(_serverKeyPair, ephKey.GetPublicKey(), authToken: _world.AgentTokenA1);
        var reply = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(req));
        var decoded = NatsAuthCalloutTestHelper.DecodeAuthResponse(reply.PayloadText, _accountKeyPair);

        Assert.That(decoded.Jwt, Is.Not.Null);
        var (_, _, pubAllow, _) = NatsAuthCalloutTestHelper.DecodeUserJwt(decoded.Jwt!, _accountKeyPair);
        Assert.That(pubAllow, Does.Contain($"tenant.{_world.ClientA.Id:N}.site.{_world.SiteA1.Id:N}.agent.{_world.AgentA1.Id:N}.remote.session.>"));

        await svc.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task T3_F01xF09_RemoteSession_Proxy_ViewerCredentials_Pass_AuthCallout_Reissuance()
    {
        using var startReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{_world.AgentA1.Id}")
        {
            Content = new StringContent("{\"kind\":\"proxy\",\"transport\":\"nats\"}", Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);
        using var startResp = await _host.HttpClient.SendAsync(startReq);
        Assert.That((int)startResp.StatusCode, Is.InRange(200, 201));

        using var doc = JsonDocument.Parse(await startResp.Content.ReadAsStringAsync());
        var viewerJwt = doc.RootElement.GetProperty("viewerCredentials").GetProperty("jwt").GetString()!;

        using var svc = CreateCalloutService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await _natsServer.WaitForSubscriptionAsync("$SYS.REQ.USER.AUTH");

        using var ephKey = KeyPair.CreatePair(PrefixByte.User);
        var authReq = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(_serverKeyPair, ephKey.GetPublicKey(), authToken: viewerJwt);
        var reply = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(authReq));
        var decoded = NatsAuthCalloutTestHelper.DecodeAuthResponse(reply.PayloadText, _accountKeyPair);

        Assert.That(decoded.Error, Is.Null);
        Assert.That(decoded.Jwt, Is.Not.Null);
        var (_, _, pubAllow, subAllow) = NatsAuthCalloutTestHelper.DecodeUserJwt(decoded.Jwt!, _accountKeyPair);
        Assert.That(pubAllow.Any(p => p.Contains(".proxy.req", StringComparison.Ordinal)), Is.True);
        Assert.That(subAllow.Any(s => s.Contains(".proxy.resp", StringComparison.Ordinal)), Is.True);

        await svc.StopAsync(CancellationToken.None);
    }
}
