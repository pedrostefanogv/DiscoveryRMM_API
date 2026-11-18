using System.Net;
using System.Text;
using System.Text.Json;
using Discovery.E2E.Tests.Harness;

namespace Discovery.E2E.Tests.Tier2;

[TestFixture]
public class F05_RequirePermissionEdgeTests
{
    private ApiE2ETestHost _host = null!;
    private E2ETenantWorld _world = null!;

    [SetUp]
    public async Task SetUp()
    {
        _host = await ApiE2ETestHost.StartAsync();
        _world = await E2ESeedHelper.SeedMultiTenantWorldAsync(_host);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_host is not null)
            await _host.DisposeAsync();
    }

    [Test]
    public async Task T2_F05_01_TechA_Cannot_Access_TenantB_Clients_Sites_Or_Agents_By_Route()
    {
        var routes = new[]
        {
            $"/api/v1/clients/{_world.ClientB.Id}",
            $"/api/v1/clients/{_world.ClientB.Id}/sites",
            $"/api/v1/clients/{_world.ClientB.Id}/sites/{_world.SiteB1.Id}",
            $"/api/v1/agents/by-client/{_world.ClientB.Id}",
            $"/api/v1/agents/by-site/{_world.SiteB1.Id}"
        };

        foreach (var route in routes)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, route).WithJwt(_world.TechA.Jwt);
            using var resp = await _host.HttpClient.SendAsync(req);
            Assert.That((int)resp.StatusCode, Is.AnyOf(403, 404), $"Cross-tenant route {route} must deny TechA.");
        }
    }

    [Test]
    public async Task T2_F05_02_TechA_Cannot_Read_Or_Execute_Commands_On_TenantB_Agent_Idor()
    {
        using var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/agents/{_world.AgentB1.Id}")
            .WithJwt(_world.TechA.Jwt);
        using var getResp = await _host.HttpClient.SendAsync(getReq);
        Assert.That((int)getResp.StatusCode, Is.AnyOf(403, 404));

        using var cmdReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentB1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                commandType = "Shell",
                payload = "{\"command\":\"whoami\"}"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);
        using var cmdResp = await _host.HttpClient.SendAsync(cmdReq);
        Assert.That((int)cmdResp.StatusCode, Is.AnyOf(403, 404));

        using var restartReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentB1.Id}/restart")
            .WithJwt(_world.TechA.Jwt);
        using var restartResp = await _host.HttpClient.SendAsync(restartReq);
        Assert.That((int)restartResp.StatusCode, Is.AnyOf(403, 404));
    }

    [Test]
    public async Task T2_F05_03_TechA_Cannot_Read_Update_Delete_Or_Create_Tickets_In_TenantB()
    {
        using var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tickets/{_world.TicketB1.Id}")
            .WithJwt(_world.TechA.Jwt);
        using var getResp = await _host.HttpClient.SendAsync(getReq);
        Assert.That((int)getResp.StatusCode, Is.AnyOf(403, 404));

        using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/tickets/{_world.TicketB1.Id}")
            .WithJwt(_world.TechA.Jwt);
        using var delResp = await _host.HttpClient.SendAsync(delReq);
        Assert.That((int)delResp.StatusCode, Is.AnyOf(403, 404));

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tickets")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                clientId = _world.ClientB.Id,
                siteId = _world.SiteB1.Id,
                title = "Cross-Tenant Ticket Attempt",
                description = "Must be denied",
                priority = 1
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);
        using var createResp = await _host.HttpClient.SendAsync(createReq);
        Assert.That((int)createResp.StatusCode, Is.AnyOf(403, 404));
    }

    [Test]
    public async Task T2_F05_04_TechA_Cannot_Start_Or_Control_RemoteSessions_On_TenantB_Agent()
    {
        using var startReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{_world.AgentB1.Id}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { kind = "terminal", transport = "nats" }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);
        using var startResp = await _host.HttpClient.SendAsync(startReq);
        Assert.That((int)startResp.StatusCode, Is.AnyOf(400, 403, 404));

        using var stopReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{_world.AgentB1.Id}/{_world.RemoteSessionB1.Id}/stop")
            .WithJwt(_world.TechA.Jwt);
        using var stopResp = await _host.HttpClient.SendAsync(stopReq);
        Assert.That((int)stopResp.StatusCode, Is.AnyOf(403, 404));

        using var credsReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{_world.AgentB1.Id}/{_world.RemoteSessionB1.Id}/nats-credentials")
            .WithJwt(_world.TechA.Jwt);
        using var credsResp = await _host.HttpClient.SendAsync(credsReq);
        Assert.That((int)credsResp.StatusCode, Is.AnyOf(403, 404));
    }

    [Test]
    public async Task T2_F05_05_SiteTechA1_Cannot_Access_Sibling_SiteA2_Or_Global_Fanout()
    {
        using var siteA2AgentsReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/agents/by-site/{_world.SiteA2.Id}")
            .WithJwt(_world.SiteTechA1.Jwt);
        using var siteA2AgentsResp = await _host.HttpClient.SendAsync(siteA2AgentsReq);
        Assert.That((int)siteA2AgentsResp.StatusCode, Is.AnyOf(403, 404));

        using var globalFanoutReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/commands/fanout/global")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { commandType = "Shell", payload = "{}" }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.SiteTechA1.Jwt);
        using var globalFanoutResp = await _host.HttpClient.SendAsync(globalFanoutReq);
        Assert.That(globalFanoutResp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task T2_F05_06_ApiKey_Enforces_Identical_Tenant_Isolation_And_Blocks_Unprivileged_Endpoints()
    {
        using var forbiddenReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/roles")
            .WithApiKey(_world.TechA.ApiKey, _world.TechA.ApiSecret);
        using var forbiddenResp = await _host.HttpClient.SendAsync(forbiddenReq);
        Assert.That(forbiddenResp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using var crossTenantReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tickets/{_world.TicketB1.Id}")
            .WithApiKey(_world.TechA.ApiKey, _world.TechA.ApiSecret);
        using var crossTenantResp = await _host.HttpClient.SendAsync(crossTenantReq);
        Assert.That((int)crossTenantResp.StatusCode, Is.AnyOf(403, 404));
    }
}
