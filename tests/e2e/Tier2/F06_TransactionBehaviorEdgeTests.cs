using System.Net;
using System.Text;
using System.Text.Json;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.E2E.Tests.Harness;
using Microsoft.EntityFrameworkCore;

namespace Discovery.E2E.Tests.Tier2;

[TestFixture]
public class F06_TransactionBehaviorEdgeTests
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
    public async Task T2_F06_01_AgentRegister_CrossTenant_SiteMismatch_Rolls_Back_DeployToken_Usage()
    {
        string rawToken = string.Empty;
        Guid tokenId = Guid.Empty;

        await _host.WithDbAsync(async db =>
        {
            // Token claims ClientA but points to SiteB1 (belongs to ClientB)
            var (entity, raw) = await E2ESeedHelper.CreateDeployTokenAsync(db, _world.ClientA.Id, _world.SiteB1.Id, maxUses: 1);
            rawToken = raw;
            tokenId = entity.Id;
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent-register")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { name = "PC-MISMATCH" }), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawToken);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        await _host.WithDbAsync(async db =>
        {
            var token = await db.DeployTokens.AsNoTracking().FirstAsync(t => t.Id == tokenId);
            Assert.That(token.UsedCount, Is.EqualTo(0));
            Assert.That(token.LastUsedAt, Is.Null);
        });
    }

    [Test]
    public async Task T2_F06_02_AgentRegister_Invalid_Token_Returns_401_And_Creates_No_Agent()
    {
        var beforeAgents = await _host.WithDbAsync(async db => await db.Agents.IgnoreQueryFilters().CountAsync());

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent-register")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { name = "PC-BADTOKEN" }), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "mdt_invalid_token_value");

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        var afterAgents = await _host.WithDbAsync(async db => await db.Agents.IgnoreQueryFilters().CountAsync());
        Assert.That(afterAgents, Is.EqualTo(beforeAgents));
    }

    [Test]
    public async Task T2_F06_03_Batch_AddRolePermissions_With_Unknown_PermissionId_Rolls_Back_Entire_Batch()
    {
        var customRoleId = Guid.NewGuid();
        Guid validPermId = Guid.Empty;

        await _host.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.Roles.Add(new Role
            {
                Id = customRoleId,
                Name = $"E2E-RollbackBatch-{Guid.NewGuid():N}",
                Type = RoleType.Custom,
                IsSystem = false,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
            validPermId = await db.Permissions.AsNoTracking().Select(p => p.Id).FirstAsync();
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/roles/{customRoleId}/permissions/batch")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                permissionIds = new[] { validPermId, Guid.NewGuid() }
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That((int)resp.StatusCode, Is.InRange(400, 404));

        var persistedCount = await _host.WithDbAsync(async db =>
            await db.RolePermissions.AsNoTracking().CountAsync(rp => rp.RoleId == customRoleId));
        Assert.That(persistedCount, Is.EqualTo(0), "Partial permission grant in batch must roll back on inner failure.");
    }

    [Test]
    public async Task T2_F06_04_CreateClient_Invalid_Body_Returns_400_And_Leaves_Db_Unchanged()
    {
        var beforeCount = await _host.WithDbAsync(async db => await db.Clients.CountAsync());

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/clients")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { name = "" }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var afterCount = await _host.WithDbAsync(async db => await db.Clients.CountAsync());
        Assert.That(afterCount, Is.EqualTo(beforeCount));
    }

    [Test]
    public async Task T2_F06_05_CreateTicket_NonExistent_Client_Fails_Without_Partial_Row()
    {
        var beforeCount = await _host.WithDbAsync(async db => await db.Tickets.CountAsync());

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tickets")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                clientId = Guid.NewGuid(),
                title = "Orphan Ticket",
                description = "Client does not exist",
                priority = 1
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That((int)resp.StatusCode, Is.InRange(400, 404));

        var afterCount = await _host.WithDbAsync(async db => await db.Tickets.CountAsync());
        Assert.That(afterCount, Is.EqualTo(beforeCount));
    }
}
