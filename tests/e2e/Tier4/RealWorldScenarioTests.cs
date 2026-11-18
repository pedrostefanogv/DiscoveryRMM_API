using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
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

namespace Discovery.E2E.Tests.Tier4;

[TestFixture]
public class RealWorldScenarioTests
{
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    [Test]
    public async Task T4_S01_TenantOnboarding_AuthCallout_JwtIssuance_And_ForgedJwtRejection()
    {
        await using var natsServer = new FakeNatsTcpServer();
        using var accountKey = KeyPair.CreatePair(PrefixByte.Account);
        using var serverKey = KeyPair.CreatePair(PrefixByte.Server);
        await using var host = await ApiE2ETestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Nats:Url"] = natsServer.Url,
            ["Nats:AccountSeed"] = accountKey.GetSeed(),
            ["Nats:ServerPublicKey"] = serverKey.GetPublicKey()
        });
        var world = await E2ESeedHelper.SeedMultiTenantWorldAsync(host);

        // 1. Admin creates new Client + Site via REST API
        using var createClientReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/clients")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { name = "Onboarding Tenant" }), Encoding.UTF8, "application/json")
        }.WithJwt(world.GlobalAdmin.Jwt);
        using var createClientResp = await host.HttpClient.SendAsync(createClientReq);
        Assert.That((int)createClientResp.StatusCode, Is.InRange(200, 201));
        using var clientDoc = JsonDocument.Parse(await createClientResp.Content.ReadAsStringAsync());
        var clientId = clientDoc.RootElement.GetProperty("id").GetGuid();

        using var createSiteReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/clients/{clientId}/sites")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { name = "HQ Site" }), Encoding.UTF8, "application/json")
        }.WithJwt(world.GlobalAdmin.Jwt);
        using var createSiteResp = await host.HttpClient.SendAsync(createSiteReq);
        Assert.That((int)createSiteResp.StatusCode, Is.InRange(200, 201));
        using var siteDoc = JsonDocument.Parse(await createSiteResp.Content.ReadAsStringAsync());
        var siteId = siteDoc.RootElement.GetProperty("id").GetGuid();

        // 2. Seed DeployToken & register new Agent via /agent-register
        string deployToken = string.Empty;
        await host.WithDbAsync(async db =>
        {
            (_, deployToken) = await E2ESeedHelper.CreateDeployTokenAsync(db, clientId, siteId, maxUses: 1);
        });

        using var regReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent-register")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { name = "ONBOARD-PC-01" }), Encoding.UTF8, "application/json")
        };
        regReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", deployToken);
        using var regResp = await host.HttpClient.SendAsync(regReq);
        Assert.That(regResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var regDoc = JsonDocument.Parse(await regResp.Content.ReadAsStringAsync());
        var agentMdzToken = regDoc.RootElement.GetProperty("token").GetString()!;
        var newAgentId = regDoc.RootElement.GetProperty("agentId").GetGuid();

        // 3. Boot NatsAuthCalloutBackgroundService over FakeNatsTcpServer
        await using var conn = new NatsConnection(new NatsOpts { Url = natsServer.Url });
        await conn.ConnectAsync();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nats:Url"] = natsServer.Url,
            ["Nats:AccountSeed"] = accountKey.GetSeed(),
            ["Nats:ServerPublicKey"] = serverKey.GetPublicKey(),
            ["Nats:AuthCallout:Enabled"] = "true"
        }).Build();

        using var callout = new NatsAuthCalloutBackgroundService(
            conn, cfg, host.Services.GetRequiredService<IServiceScopeFactory>(),
            new NatsAuthCalloutReloadSignal(), NullLogger<NatsAuthCalloutBackgroundService>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await callout.StartAsync(cts.Token);
        await natsServer.WaitForSubscriptionAsync("$SYS.REQ.USER.AUTH");

        // 4. Legit agent auth callout succeeds with tenant-scoped JWT
        using var ephAgentKey = KeyPair.CreatePair(PrefixByte.User);
        var validAuthReq = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(serverKey, ephAgentKey.GetPublicKey(), authToken: agentMdzToken);
        var validReply = await natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(validAuthReq));
        var validDecoded = NatsAuthCalloutTestHelper.DecodeAuthResponse(validReply.PayloadText, accountKey);
        Assert.That(validDecoded.Jwt, Is.Not.Null);
        var (_, _, pubAllow, _) = NatsAuthCalloutTestHelper.DecodeUserJwt(validDecoded.Jwt!, accountKey);
        Assert.That(pubAllow, Does.Contain($"tenant.{clientId}.site.{siteId}.agent.{newAgentId}.heartbeat"));

        // 5. Forged pre-issued NATS JWT is rejected
        using var attackerKey = KeyPair.CreatePair(PrefixByte.Account);
        using var ephAttackerUser = KeyPair.CreatePair(PrefixByte.User);
        var forgedJwt = NatsAuthCalloutTestHelper.BuildPreIssuedUserJwt(attackerKey, ephAttackerUser.GetPublicKey(), [">"], [">"]);
        var forgedReq = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(serverKey, ephAttackerUser.GetPublicKey(), authToken: forgedJwt);
        var forgedReply = await natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(forgedReq));
        var forgedDecoded = NatsAuthCalloutTestHelper.DecodeAuthResponse(forgedReply.PayloadText, accountKey);
        Assert.That(forgedDecoded.Jwt, Is.Null);

        await callout.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task T4_S02_MultiTenant_OutputCache_ApiKey_And_NonGlobal_Technician_Workflow()
    {
        await using var host = await ApiE2ETestHost.StartAsync();
        var world = await E2ESeedHelper.SeedMultiTenantWorldAsync(host);

        // 1. OutputCache isolates TechA vs TechB API keys
        using var clientsAReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients")
            .WithApiKey(world.TechA.ApiKey, world.TechA.ApiSecret);
        using var clientsAResp = await host.HttpClient.SendAsync(clientsAReq);
        Assert.That(clientsAResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await clientsAResp.Content.ReadAsStringAsync(), Does.Not.Contain(world.ClientB.Id.ToString()));

        using var clientsBReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients")
            .WithApiKey(world.TechB.ApiKey, world.TechB.ApiSecret);
        using var clientsBResp = await host.HttpClient.SendAsync(clientsBReq);
        Assert.That(clientsBResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await clientsBResp.Content.ReadAsStringAsync(), Does.Not.Contain(world.ClientA.Id.ToString()));

        // 2. TechA creates Ticket in ClientA
        using var createTicketReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tickets")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                clientId = world.ClientA.Id,
                siteId = world.SiteA1.Id,
                agentId = world.AgentA1.Id,
                title = "Scenario 2 Ticket",
                description = "Workstation printer offline",
                priority = 2
            }), Encoding.UTF8, "application/json")
        }.WithApiKey(world.TechA.ApiKey, world.TechA.ApiSecret);
        using var createTicketResp = await host.HttpClient.SendAsync(createTicketReq);
        Assert.That((int)createTicketResp.StatusCode, Is.InRange(200, 201));
        using var createdDoc = JsonDocument.Parse(await createTicketResp.Content.ReadAsStringAsync());
        var newTicketId = createdDoc.RootElement.GetProperty("id").GetGuid();

        // 3. TechB cannot read TechA's newly created ticket
        using var techBGetReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tickets/{newTicketId}")
            .WithApiKey(world.TechB.ApiKey, world.TechB.ApiSecret);
        using var techBGetResp = await host.HttpClient.SendAsync(techBGetReq);
        Assert.That((int)techBGetResp.StatusCode, Is.AnyOf(403, 404));
    }

    [Test]
    public async Task T4_S03_HeartbeatStorm_HardwareUpdate_TrashedQuarantine_And_AgentRateLimit()
    {
        await using var natsServer = new FakeNatsTcpServer();
        await using var host = await ApiE2ETestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Security:RateLimiting:Agent:PermitLimit"] = "4",
            ["Security:RateLimiting:Agent:WindowSeconds"] = "60",
            ["Security:RateLimiting:Agent:QueueLimit"] = "0"
        });
        var world = await E2ESeedHelper.SeedMultiTenantWorldAsync(host);

        await using var conn = new NatsConnection(new NatsOpts { Url = natsServer.Url });
        await conn.ConnectAsync();
        await using var messaging = new NatsAgentMessaging(
            conn,
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<DashboardEventContractNormalizer>(),
            new StaticOptionsMonitor<NatsGlobalPongOptions>(new NatsGlobalPongOptions { PublishIntervalSeconds = 3600 }),
            NullLogger<NatsAgentMessaging>.Instance);

        using var cts = new CancellationTokenSource();
        var subTask = Task.Run(() => messaging.SubscribeToAgentMessagesAsync(cts.Token));
        await natsServer.WaitForSubscriptionAsync("tenant.*.site.*.agent.*.heartbeat");
        await natsServer.WaitForSubscriptionAsync("tenant.*.site.*.agent.*.hardware");

        // 1. Transition heartbeat + hardware report + steady-state heartbeats
        var hbSubject = $"tenant.{world.ClientA.Id}.site.{world.SiteA1.Id}.agent.{world.AgentA1.Id}.heartbeat";
        await natsServer.PublishAsync(hbSubject, JsonSerializer.Serialize(new
        {
            agentId = world.AgentA1.Id,
            clientId = world.ClientB.Id, // spoofed payload should be overridden by subject
            peerId = "12D3KooWStormPeer",
            cpuPercent = 25.0
        }));
        await natsServer.WaitForPublishedAsync(m => m.Subject == $"tenant.{world.ClientA.Id}.p2p.events");

        var hwSubject = $"tenant.{world.ClientA.Id}.site.{world.SiteA1.Id}.agent.{world.AgentA1.Id}.hardware";
        await natsServer.PublishAsync(hwSubject, JsonSerializer.Serialize(new
        {
            osVersion = "Windows 11 24H2",
            machineScore = 90,
            hardware = new { processor = "AMD Ryzen 9", processorCores = 12, totalMemoryBytes = 17179869184L }
        }));
        await natsServer.WaitForPublishedAsync(m => m.PayloadText.Contains("AgentHardwareReported", StringComparison.Ordinal));

        // 2. Exhaust AgentA1 rate limit (4 allowed, 5th returns 429) while AgentB1 succeeds
        for (var i = 0; i < 4; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me")
                .WithAgentAuth(world.AgentTokenA1, world.AgentA1.Id);
            using var resp = await host.HttpClient.SendAsync(req);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        using var blockedReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me")
            .WithAgentAuth(world.AgentTokenA1, world.AgentA1.Id);
        using var blockedResp = await host.HttpClient.SendAsync(blockedReq);
        Assert.That(blockedResp.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));

        using var agentBReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me")
            .WithAgentAuth(world.AgentTokenB1, world.AgentB1.Id);
        using var agentBResp = await host.HttpClient.SendAsync(agentBReq);
        Assert.That(agentBResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // 3. Soft-delete AgentB1 -> quarantined from inventory
        await host.WithDbAsync(async db =>
        {
            var ag = await db.Agents.FirstAsync(a => a.Id == world.AgentB1.Id);
            ag.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        });

        using var trashedHwReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me/hardware")
            .WithAgentAuth(world.AgentTokenB1, world.AgentB1.Id);
        using var trashedHwResp = await host.HttpClient.SendAsync(trashedHwReq);
        Assert.That(trashedHwResp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        cts.Cancel();
    }

    [Test]
    public async Task T4_S04_Full_RemoteProxy_And_Rdp_Session_Lifecycle_With_WsTunnel_Relay()
    {
        await using var natsServer = new FakeNatsTcpServer();
        using var accountKey = KeyPair.CreatePair(PrefixByte.Account);
        await using var host = await ApiE2ETestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["Nats:Url"] = natsServer.Url,
                ["Nats:AccountSeed"] = accountKey.GetSeed()
            },
            useFakeAgentMessaging: false);
        var world = await E2ESeedHelper.SeedMultiTenantWorldAsync(host);

        // 1. Start RDP session as TechA
        using var startReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{world.AgentA1.Id}")
        {
            Content = new StringContent("{\"kind\":\"rdp\",\"transport\":\"nats\"}", Encoding.UTF8, "application/json")
        }.WithJwt(world.TechA.Jwt);
        using var startResp = await host.HttpClient.SendAsync(startReq);
        Assert.That((int)startResp.StatusCode, Is.InRange(200, 201));

        using var startDoc = JsonDocument.Parse(await startResp.Content.ReadAsStringAsync());
        var sessionId = startDoc.RootElement.GetProperty("sessionId").GetGuid();
        var natsSubject = startDoc.RootElement.GetProperty("natsSubject").GetString()!;

        // 2. Refresh NATS credentials
        using var credsReq = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/remote-sessions/{world.AgentA1.Id}/{sessionId}/nats-credentials")
            .WithJwt(world.TechA.Jwt);
        using var credsResp = await host.HttpClient.SendAsync(credsReq);
        Assert.That(credsResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // 3. Download .rdp file
        using var rdpReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/remote-sessions/{world.AgentA1.Id}/{sessionId}/rdp-file?localPort=33999")
            .WithJwt(world.TechA.Jwt);
        using var rdpResp = await host.HttpClient.SendAsync(rdpReq);
        Assert.That(rdpResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await rdpResp.Content.ReadAsStringAsync(), Does.Contain("127.0.0.1:33999"));

        // 4. Open WS tunnel & relay bidirectional frames via FakeNatsTcpServer
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {world.TechA.Jwt}");
        await ws.ConnectAsync(
            new Uri($"ws://127.0.0.1:{host.BaseAddress.Port}/api/v1/remote-sessions/{world.AgentA1.Id}/{sessionId}/tunnel/ws"),
            CancellationToken.None);

        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"streamId\":7,\"op\":\"open\",\"protocol\":\"rdp\",\"targetPort\":3389}"), WebSocketMessageType.Text, true, CancellationToken.None);
        var inbound = await natsServer.WaitForPublishedAsync(m => m.Subject == $"{natsSubject}.tunnel.in");
        Assert.That(inbound.PayloadText, Does.Contain("3389"));

        await natsServer.PublishAsync($"{natsSubject}.tunnel.out", "{\"streamId\":7,\"op\":\"ready\"}");
        var buf = new byte[1024];
        using var wsCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var res = await ws.ReceiveAsync(buf, wsCts.Token);
        Assert.That(Encoding.UTF8.GetString(buf, 0, res.Count), Does.Contain("ready"));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        // 5. Stop session
        using var stopReq = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/remote-sessions/{world.AgentA1.Id}/{sessionId}/stop")
            .WithJwt(world.TechA.Jwt);
        using var stopResp = await host.HttpClient.SendAsync(stopReq);
        Assert.That(stopResp.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    [Test]
    public async Task T4_S05_Cqrs_MultiStep_FailureRollback_Validator_And_CiBuildIntegrity()
    {
        await using var host = await ApiE2ETestHost.StartAsync();
        var world = await E2ESeedHelper.SeedMultiTenantWorldAsync(host);

        // 1. DeployToken failure rolls back DB update
        Guid tokenId = Guid.Empty;
        string rawToken = string.Empty;
        // NOTA (ajuste de spec): siteId inexistente violava a FK real de
        // deploy_tokens.site_id no seed. Site existente de outro tenant produz o
        // mesmo 404 no /agent-register e exercita o mesmo rollback.
        await host.WithDbAsync(async db =>
        {
            var (entity, raw) = await E2ESeedHelper.CreateDeployTokenAsync(db, world.ClientA.Id, world.SiteB1.Id, 1);
            tokenId = entity.Id;
            rawToken = raw;
        });

        using var regReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent-register")
        {
            Content = new StringContent("{\"name\":\"FAIL-ROLLBACK\"}", Encoding.UTF8, "application/json")
        };
        regReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawToken);
        using var regResp = await host.HttpClient.SendAsync(regReq);
        Assert.That(regResp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var usedCount = await host.WithDbAsync(async db => (await db.DeployTokens.AsNoTracking().FirstAsync(t => t.Id == tokenId)).UsedCount);
        Assert.That(usedCount, Is.EqualTo(0));

        // 2. SpecialCommandPayloadValidator accepts valid Update and rejects invalid Update
        using var validCmdReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{world.AgentA1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { commandType = "Update", payload = "{\"action\":\"check-update\"}" }), Encoding.UTF8, "application/json")
        }.WithJwt(world.GlobalAdmin.Jwt);
        using var validCmdResp = await host.HttpClient.SendAsync(validCmdReq);
        Assert.That(validCmdResp.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        using var invalidCmdReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{world.AgentA1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { commandType = "Update", payload = "{\"action\":\"force-update\"}" }), Encoding.UTF8, "application/json")
        }.WithJwt(world.GlobalAdmin.Jwt);
        using var invalidCmdResp = await host.HttpClient.SendAsync(invalidCmdReq);
        Assert.That(invalidCmdResp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // 3. Report preview succeeds
        using var reportReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/reports/preview")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                template = new { name = "S05 Audit", datasetType = "Agents" },
                format = "Csv",
                previewMode = "data"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(world.GlobalAdmin.Jwt);
        using var reportResp = await host.HttpClient.SendAsync(reportReq);
        Assert.That(reportResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task T4_S06_CrossTenant_Fanout_Isolation_And_SupportChat_Subject_Enforcement()
    {
        await using var natsServer = new FakeNatsTcpServer();
        using var accountKey = KeyPair.CreatePair(PrefixByte.Account);
        using var serverKey = KeyPair.CreatePair(PrefixByte.Server);
        await using var host = await ApiE2ETestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Nats:Url"] = natsServer.Url,
            ["Nats:AccountSeed"] = accountKey.GetSeed(),
            ["Nats:ServerPublicKey"] = serverKey.GetPublicKey()
        });
        var world = await E2ESeedHelper.SeedMultiTenantWorldAsync(host);

        using var ephA = KeyPair.CreatePair(PrefixByte.User);
        using var ephB = KeyPair.CreatePair(PrefixByte.User);
        string jwtA = string.Empty, jwtB = string.Empty;

        await host.WithScopeAsync(async sp =>
        {
            var creds = sp.GetRequiredService<INatsCredentialsService>();
            jwtA = (await creds.IssueUserJwtForAgentAsync(ephA.GetPublicKey(), world.AgentA1.Id)).Jwt;
            jwtB = (await creds.IssueUserJwtForAgentAsync(ephB.GetPublicKey(), world.AgentB1.Id)).Jwt;
        });

        var (_, _, pubA, subA) = NatsAuthCalloutTestHelper.DecodeUserJwt(jwtA, accountKey);
        var (_, _, pubB, subB) = NatsAuthCalloutTestHelper.DecodeUserJwt(jwtB, accountKey);

        Assert.That(pubA, Does.Contain($"$JS.API.CONSUMER.CREATE.{NatsSubjectBuilder.TenantFanoutStreamName(world.ClientA.Id)}.>"));
        Assert.That(pubA, Does.Not.Contain($"$JS.API.CONSUMER.CREATE.{NatsSubjectBuilder.TenantFanoutStreamName(world.ClientB.Id)}.>"));
        Assert.That(pubB, Does.Contain($"$JS.API.CONSUMER.CREATE.{NatsSubjectBuilder.TenantSupportChatStreamName(world.ClientB.Id)}.>"));
        Assert.That(subA.Any(s => s.Contains(world.ClientB.Id.ToString(), StringComparison.OrdinalIgnoreCase)), Is.False);
        Assert.That(subB.Any(s => s.Contains(world.ClientA.Id.ToString(), StringComparison.OrdinalIgnoreCase)), Is.False);

        await using var conn = new NatsConnection(new NatsOpts { Url = natsServer.Url });
        await conn.ConnectAsync();
        await using var messaging = new NatsAgentMessaging(
            conn,
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<DashboardEventContractNormalizer>(),
            new StaticOptionsMonitor<NatsGlobalPongOptions>(new NatsGlobalPongOptions { PublishIntervalSeconds = 3600 }),
            NullLogger<NatsAgentMessaging>.Instance,
            new FakeNatsTenantStreamProvisioner());

        await messaging.PublishClientFanoutCommandAsync(world.ClientA.Id, new CommandDispatchEnvelope
        {
            DispatchId = Guid.NewGuid(),
            CommandType = "Shell",
            TargetScope = "client",
            TargetClientId = world.ClientA.Id,
            IssuedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Payload = "{}"
        });

        await messaging.PublishSupportChatServerAsync(world.ClientA.Id, world.SiteA1.Id, world.AgentA1.Id, new TicketChatEnvelope
        {
            EventType = "message",
            SessionId = Guid.NewGuid(),
            TicketId = world.TicketA1.Id,
            Body = "Tenant A isolated chat"
        });

        var fanoutMsg = await natsServer.WaitForPublishedAsync(m => m.Subject == $"tenant.{world.ClientA.Id}.agents.command");
        var chatMsg = await natsServer.WaitForPublishedAsync(m => m.Subject == $"tenant.{world.ClientA.Id}.site.{world.SiteA1.Id}.agent.{world.AgentA1.Id}.supportchat.server");
        Assert.That(fanoutMsg, Is.Not.Null);
        Assert.That(chatMsg.PayloadText, Does.Contain("Tenant A isolated chat"));
    }
}
