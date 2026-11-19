using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Infrastructure.Data;
using Discovery.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.IntegrationTests;

[TestFixture]
[Category("Integration")]
[Category("PostgreSQL")]
public sealed class PostgresRbacAndTenantIsolationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private DiscoveryPostgresWebApplicationFactory? _factory;
    private TenantWorld? _world;

    private sealed record TenantWorld(
        Guid ClientAId,
        Guid ClientBId,
        Guid SiteA1Id,
        Guid SiteA2Id,
        Guid SiteB1Id,
        Guid ActiveAgentA1Id,
        Guid ActiveAgentA2Id,
        Guid ActiveAgentB1Id,
        Guid DeletedAgentA1Id,
        Guid DeletedAgentA2Id,
        Guid DeletedAgentB1Id,
        string GlobalAdminJwt,
        string ClientTechAJwt,
        string SiteTechA1Jwt,
        string NoAgentsPermJwt);

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        await PostgresContainerFixture.EnsureAvailableOrIgnoreAsync();
        _factory = await DiscoveryPostgresWebApplicationFactory.CreateAsync("rbac");
        _world = await SeedTenantWorldAsync(_factory.Services);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Test]
    public async Task UnauthenticatedRequest_Returns401Unauthorized()
    {
        using var client = _factory!.CreateClient();
        using var response = await client.GetAsync("/api/v1/agents/deleted");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task UserWithoutAgentsViewPermission_Returns403Forbidden()
    {
        using var client = _factory!.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agents/deleted")
            .WithBearer(_world!.NoAgentsPermJwt);

        using var response = await client.SendAsync(req);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task GetDeletedAgents_RespectsAllowedSiteIdsAndAllowedClientIdsOnRealPostgres()
    {
        using var client = _factory!.CreateClient();

        // 1. Site-scoped technician (AllowedSiteIds = [SiteA1], AllowedClientIds = [])
        using var siteReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agents/deleted?page=1&pageSize=50")
            .WithBearer(_world!.SiteTechA1Jwt);
        using var siteResp = await client.SendAsync(siteReq);
        Assert.That(siteResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var sitePage = await siteResp.Content.ReadFromJsonAsync<DeletedAgentsPageDto>(JsonOptions);
        Assert.That(sitePage, Is.Not.Null);
        Assert.That(sitePage!.Total, Is.EqualTo(1));
        Assert.That(sitePage.Items.Select(a => a.Id), Is.EquivalentTo(new[] { _world.DeletedAgentA1Id }));

        // 2. Client-scoped technician (AllowedClientIds = [ClientA])
        using var clientReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agents/deleted?page=1&pageSize=50")
            .WithBearer(_world.ClientTechAJwt);
        using var clientResp = await client.SendAsync(clientReq);
        Assert.That(clientResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var clientPage = await clientResp.Content.ReadFromJsonAsync<DeletedAgentsPageDto>(JsonOptions);
        Assert.That(clientPage, Is.Not.Null);
        Assert.That(clientPage!.Total, Is.EqualTo(2));
        Assert.That(
            clientPage.Items.Select(a => a.Id),
            Is.EquivalentTo(new[] { _world.DeletedAgentA1Id, _world.DeletedAgentA2Id }));

        // 3. Global admin sees all 3 soft-deleted agents across both tenants
        using var adminReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agents/deleted?page=1&pageSize=50")
            .WithBearer(_world.GlobalAdminJwt);
        using var adminResp = await client.SendAsync(adminReq);
        Assert.That(adminResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var adminPage = await adminResp.Content.ReadFromJsonAsync<DeletedAgentsPageDto>(JsonOptions);
        Assert.That(adminPage, Is.Not.Null);
        Assert.That(adminPage!.Total, Is.EqualTo(3));
        Assert.That(
            adminPage.Items.Select(a => a.Id),
            Is.EquivalentTo(new[] { _world.DeletedAgentA1Id, _world.DeletedAgentA2Id, _world.DeletedAgentB1Id }));
    }

    [Test]
    public async Task CrossTenantAgentQueries_AreBlockedByTenantScopePolicyOnRealPostgres()
    {
        using var client = _factory!.CreateClient();

        // ClientTechA cannot read ClientB's agent, site, or client endpoints
        foreach (var path in new[]
        {
            $"/api/v1/agents/{_world!.ActiveAgentB1Id}",
            $"/api/v1/agents/by-site/{_world.SiteB1Id}",
            $"/api/v1/agents/by-client/{_world.ClientBId}"
        })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path).WithBearer(_world.ClientTechAJwt);
            using var resp = await client.SendAsync(req);
            Assert.That(
                resp.StatusCode,
                Is.EqualTo(HttpStatusCode.Forbidden).Or.EqualTo(HttpStatusCode.NotFound),
                $"Cross-tenant access to {path} must be denied.");
        }

        // SiteTechA1 cannot read SiteA2's agent or site endpoint, even within the same ClientA
        foreach (var path in new[]
        {
            $"/api/v1/agents/{_world.ActiveAgentA2Id}",
            $"/api/v1/agents/by-site/{_world.SiteA2Id}"
        })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path).WithBearer(_world.SiteTechA1Jwt);
            using var resp = await client.SendAsync(req);
            Assert.That(
                resp.StatusCode,
                Is.EqualTo(HttpStatusCode.Forbidden).Or.EqualTo(HttpStatusCode.NotFound),
                $"Cross-site access to {path} must be denied for SiteTechA1.");
        }

        // SiteTechA1 CAN read its own SiteA1 agent
        using var ownReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/agents/{_world.ActiveAgentA1Id}")
            .WithBearer(_world.SiteTechA1Jwt);
        using var ownResp = await client.SendAsync(ownReq);
        Assert.That(ownResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    private static async Task<TenantWorld> SeedTenantWorldAsync(IServiceProvider rootServices)
    {
        await using var scope = rootServices.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<DiscoveryDbContext>();
        var now = DateTime.UtcNow;

        var allPerms = await IntegrationSeedHelper.EnsureAllPermissionsAsync(db);

        var adminRole = await IntegrationSeedHelper.CreateRoleAsync(db, "IT-GlobalAdmin", RoleType.System, allPerms);
        var techRole = await IntegrationSeedHelper.CreateRoleAsync(
            db,
            "IT-Tech",
            RoleType.Custom,
            allPerms.Where(p => p.ResourceType == ResourceType.Agents && p.ActionType == ActionType.View));
        var noAgentsRole = await IntegrationSeedHelper.CreateRoleAsync(
            db,
            "IT-NoAgents",
            RoleType.Custom,
            allPerms.Where(p => p.ResourceType == ResourceType.Dashboard && p.ActionType == ActionType.View));

        var clientA = new Client { Id = Guid.NewGuid(), Name = "PG Tenant Alpha", IsActive = true, CreatedAt = now, UpdatedAt = now };
        var clientB = new Client { Id = Guid.NewGuid(), Name = "PG Tenant Beta", IsActive = true, CreatedAt = now, UpdatedAt = now };
        db.Clients.AddRange(clientA, clientB);

        var siteA1 = new Site { Id = Guid.NewGuid(), ClientId = clientA.Id, Name = "PG Alpha Site 1", IsActive = true, CreatedAt = now, UpdatedAt = now };
        var siteA2 = new Site { Id = Guid.NewGuid(), ClientId = clientA.Id, Name = "PG Alpha Site 2", IsActive = true, CreatedAt = now, UpdatedAt = now };
        var siteB1 = new Site { Id = Guid.NewGuid(), ClientId = clientB.Id, Name = "PG Beta Site 1", IsActive = true, CreatedAt = now, UpdatedAt = now };
        db.Sites.AddRange(siteA1, siteA2, siteB1);

        var activeA1 = NewAgent(siteA1.Id, "PG-ACTIVE-A1", deletedAt: null, now);
        var activeA2 = NewAgent(siteA2.Id, "PG-ACTIVE-A2", deletedAt: null, now);
        var activeB1 = NewAgent(siteB1.Id, "PG-ACTIVE-B1", deletedAt: null, now);
        var deletedA1 = NewAgent(siteA1.Id, "PG-DELETED-A1", deletedAt: now.AddMinutes(-10), now);
        var deletedA2 = NewAgent(siteA2.Id, "PG-DELETED-A2", deletedAt: now.AddMinutes(-5), now);
        var deletedB1 = NewAgent(siteB1.Id, "PG-DELETED-B1", deletedAt: now.AddMinutes(-1), now);
        db.Agents.AddRange(activeA1, activeA2, activeB1, deletedA1, deletedA2, deletedB1);
        await db.SaveChangesAsync();

        var globalAdmin = await IntegrationSeedHelper.CreateUserWithJwtAsync(sp, "pg-admin", adminRole, ScopeLevel.Global, null);
        var clientTechA = await IntegrationSeedHelper.CreateUserWithJwtAsync(sp, "pg-client-tech-a", techRole, ScopeLevel.Client, clientA.Id);
        var siteTechA1 = await IntegrationSeedHelper.CreateUserWithJwtAsync(sp, "pg-site-tech-a1", techRole, ScopeLevel.Site, siteA1.Id);
        var noAgentsUser = await IntegrationSeedHelper.CreateUserWithJwtAsync(sp, "pg-no-agents", noAgentsRole, ScopeLevel.Global, null);

        return new TenantWorld(
            clientA.Id,
            clientB.Id,
            siteA1.Id,
            siteA2.Id,
            siteB1.Id,
            activeA1.Id,
            activeA2.Id,
            activeB1.Id,
            deletedA1.Id,
            deletedA2.Id,
            deletedB1.Id,
            globalAdmin.Jwt,
            clientTechA.Jwt,
            siteTechA1.Jwt,
            noAgentsUser.Jwt);
    }

    private static Agent NewAgent(Guid siteId, string hostname, DateTime? deletedAt, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        SiteId = siteId,
        Hostname = hostname,
        DisplayName = hostname,
        Status = AgentStatus.Offline,
        CreatedAt = now,
        UpdatedAt = now,
        DeletedAt = deletedAt
    };
}
