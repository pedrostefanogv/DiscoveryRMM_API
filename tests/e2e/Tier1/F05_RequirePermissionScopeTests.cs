using System.Net;
using System.Text;
using System.Text.Json;
using Discovery.E2E.Tests.Harness;

namespace Discovery.E2E.Tests.Tier1;

[TestFixture]
public class F05_RequirePermissionScopeTests
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
    public async Task T1_F05_01_ClientScoped_Tech_Can_List_And_Get_Own_Clients_And_Sites()
    {
        using var clientsReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients").WithJwt(_world.TechA.Jwt);
        using var clientsResp = await _host.HttpClient.SendAsync(clientsReq);
        Assert.That(clientsResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var clientsBody = await clientsResp.Content.ReadAsStringAsync();
        Assert.That(clientsBody, Does.Contain(_world.ClientA.Id.ToString()));
        Assert.That(clientsBody, Does.Not.Contain(_world.ClientB.Id.ToString()));

        using var clientReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/clients/{_world.ClientA.Id}").WithJwt(_world.TechA.Jwt);
        using var clientResp = await _host.HttpClient.SendAsync(clientReq);
        Assert.That(clientResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var sitesReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sites").WithJwt(_world.TechA.Jwt);
        using var sitesResp = await _host.HttpClient.SendAsync(sitesReq);
        Assert.That(sitesResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var sitesBody = await sitesResp.Content.ReadAsStringAsync();
        Assert.That(sitesBody, Does.Contain(_world.SiteA1.Id.ToString()));
        Assert.That(sitesBody, Does.Contain(_world.SiteA2.Id.ToString()));
        Assert.That(sitesBody, Does.Not.Contain(_world.SiteB1.Id.ToString()));
    }

    [Test]
    public async Task T1_F05_02_ClientScoped_Tech_Can_Access_Own_Agents_And_BySite_Agents()
    {
        using var byClientReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/agents/by-client/{_world.ClientA.Id}").WithJwt(_world.TechA.Jwt);
        using var byClientResp = await _host.HttpClient.SendAsync(byClientReq);
        Assert.That(byClientResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var bySiteReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/agents/by-site/{_world.SiteA1.Id}").WithJwt(_world.TechA.Jwt);
        using var bySiteResp = await _host.HttpClient.SendAsync(bySiteReq);
        Assert.That(bySiteResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var bySiteBody = await bySiteResp.Content.ReadAsStringAsync();
        Assert.That(bySiteBody, Does.Contain(_world.AgentA1.Id.ToString()));

        using var agentReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/agents/{_world.AgentA1.Id}").WithJwt(_world.TechA.Jwt);
        using var agentResp = await _host.HttpClient.SendAsync(agentReq);
        Assert.That(agentResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task T1_F05_03_ClientScoped_Tech_Can_List_Get_And_Create_Own_Tickets()
    {
        using var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tickets").WithJwt(_world.TechA.Jwt);
        using var listResp = await _host.HttpClient.SendAsync(listReq);
        Assert.That(listResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var listBody = await listResp.Content.ReadAsStringAsync();
        Assert.That(listBody, Does.Contain(_world.TicketA1.Id.ToString()));
        Assert.That(listBody, Does.Not.Contain(_world.TicketB1.Id.ToString()));

        using var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tickets/{_world.TicketA1.Id}").WithJwt(_world.TechA.Jwt);
        using var getResp = await _host.HttpClient.SendAsync(getReq);
        Assert.That(getResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tickets")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                clientId = _world.ClientA.Id,
                siteId = _world.SiteA1.Id,
                agentId = _world.AgentA1.Id,
                title = "TechA Created Ticket",
                description = "Created by scoped technician",
                priority = 1
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);

        using var createResp = await _host.HttpClient.SendAsync(createReq);
        Assert.That((int)createResp.StatusCode, Is.InRange(200, 201));
    }

    [Test]
    public async Task T1_F05_04_SiteScoped_Tech_Sees_Only_Assigned_Site_In_Sites_And_Tickets()
    {
        using var sitesReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sites").WithJwt(_world.SiteTechA1.Jwt);
        using var sitesResp = await _host.HttpClient.SendAsync(sitesReq);
        Assert.That(sitesResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var sitesBody = await sitesResp.Content.ReadAsStringAsync();
        Assert.That(sitesBody, Does.Contain(_world.SiteA1.Id.ToString()));
        Assert.That(sitesBody, Does.Not.Contain(_world.SiteA2.Id.ToString()));
        Assert.That(sitesBody, Does.Not.Contain(_world.SiteB1.Id.ToString()));

        using var ticketsReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tickets").WithJwt(_world.SiteTechA1.Jwt);
        using var ticketsResp = await _host.HttpClient.SendAsync(ticketsReq);
        Assert.That(ticketsResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var ticketsBody = await ticketsResp.Content.ReadAsStringAsync();
        Assert.That(ticketsBody, Does.Contain(_world.TicketA1.Id.ToString()));
        Assert.That(ticketsBody, Does.Not.Contain(_world.TicketB1.Id.ToString()));
    }

    [Test]
    public async Task T1_F05_05_ClientScoped_Tech_Can_Manage_Own_RemoteSessions()
    {
        using var startReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{_world.AgentA1.Id}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                kind = "terminal",
                transport = "nats"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);

        using var startResp = await _host.HttpClient.SendAsync(startReq);
        Assert.That((int)startResp.StatusCode, Is.InRange(200, 201));

        using var activeReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/remote-sessions/{_world.AgentA1.Id}/active")
            .WithJwt(_world.TechA.Jwt);
        using var activeResp = await _host.HttpClient.SendAsync(activeReq);
        Assert.That(activeResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task T1_F05_06_GlobalAdmin_Can_Access_Global_Admin_Endpoints_While_Scoped_Tech_Gets_403()
    {
        var globalRoutes = new[]
        {
            "/api/v1/roles",
            "/api/v1/configurations/server",
            "/api/v1/configuration-audit"
        };

        foreach (var route in globalRoutes)
        {
            using var adminReq = new HttpRequestMessage(HttpMethod.Get, route).WithJwt(_world.GlobalAdmin.Jwt);
            using var adminResp = await _host.HttpClient.SendAsync(adminReq);
            Assert.That(adminResp.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"GlobalAdmin should access {route}");

            using var techReq = new HttpRequestMessage(HttpMethod.Get, route).WithJwt(_world.TechA.Jwt);
            using var techResp = await _host.HttpClient.SendAsync(techReq);
            Assert.That(techResp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), $"Scoped TechA must get 403 on {route}");
        }
    }
}
