using System.Net;
using System.Text;
using System.Text.Json;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.E2E.Tests.Harness;
using Microsoft.EntityFrameworkCore;

namespace Discovery.E2E.Tests.Tier1;

[TestFixture]
public class F06_TransactionBehaviorTests
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
    public async Task T1_F06_01_AgentRegister_Failure_Rolls_Back_DeployToken_UsedCount()
    {
        // NOTA (ajuste de spec): o seed original usava um siteId inexistente
        // (Guid.NewGuid()). DeployTokens.SiteId tem FK REAL no modelo de produção
        // (deploy_tokens.site_id -> sites.id, DeleteBehavior.Restrict), então o
        // seed violava a FK e o teste falhava ANTES de exercitar o rollback —
        // em Postgres de produção daria o mesmo erro. O caminho de falha
        // equivalente (404 "Site não encontrado ou não pertence ao cliente do
        // token") é acionado com um site EXISTENTE de OUTRO tenant, que mantém
        // todas as FKs válidas e exercita exatamente o mesmo rollback do
        // TransactionBehavior.
        string rawDeployToken = string.Empty;
        Guid tokenId = Guid.Empty;

        await _host.WithDbAsync(async db =>
        {
            var (entity, raw) = await E2ESeedHelper.CreateDeployTokenAsync(db, _world.ClientA.Id, _world.SiteB1.Id, maxUses: 1);
            rawDeployToken = raw;
            tokenId = entity.Id;
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent-register")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                name = "PC-ROLLBACK-01",
                macAddress = "AA:BB:CC:DD:EE:11"
            }), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawDeployToken);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        await _host.WithDbAsync(async db =>
        {
            var token = await db.DeployTokens.AsNoTracking().FirstAsync(t => t.Id == tokenId);
            Assert.That(token.UsedCount, Is.EqualTo(0), "DeployToken UsedCount must roll back on Result.IsFailure.");
            Assert.That(token.LastUsedAt, Is.Null, "DeployToken LastUsedAt must roll back on Result.IsFailure.");
        });
    }

    [Test]
    public async Task T1_F06_02_AgentRegister_Success_Commits_Agent_And_Increments_DeployToken_UsedCount()
    {
        string rawDeployToken = string.Empty;
        Guid tokenId = Guid.Empty;

        await _host.WithDbAsync(async db =>
        {
            var (entity, raw) = await E2ESeedHelper.CreateDeployTokenAsync(db, _world.ClientA.Id, _world.SiteA1.Id, maxUses: 1);
            rawDeployToken = raw;
            tokenId = entity.Id;
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent-register")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                name = "PC-COMMIT-01",
                macAddress = "AA:BB:CC:DD:EE:22"
            }), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawDeployToken);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var newAgentId = doc.RootElement.GetProperty("agentId").GetGuid();

        await _host.WithDbAsync(async db =>
        {
            var token = await db.DeployTokens.AsNoTracking().FirstAsync(t => t.Id == tokenId);
            Assert.That(token.UsedCount, Is.EqualTo(1));
            Assert.That(token.LastUsedAt, Is.Not.Null);
            var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == newAgentId);
            Assert.That(agent, Is.Not.Null);
        });
    }

    [Test]
    public async Task T1_F06_03_Nested_Batch_AddRolePermissions_Succeeds_And_Persists_Atomically()
    {
        var customRoleId = Guid.NewGuid();
        Guid[] permIds = [];

        await _host.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.Roles.Add(new Role
            {
                Id = customRoleId,
                Name = $"E2E-CustomRole-{Guid.NewGuid():N}",
                Type = RoleType.Custom,
                IsSystem = false,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();

            permIds = await db.Permissions.AsNoTracking().Take(3).Select(p => p.Id).ToArrayAsync();
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/roles/{customRoleId}/permissions/batch")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { permissionIds = permIds }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.That(doc.RootElement.GetProperty("added").GetInt32(), Is.EqualTo(permIds.Length));

        var count = await _host.WithDbAsync(async db =>
            await db.RolePermissions.AsNoTracking().CountAsync(rp => rp.RoleId == customRoleId));
        Assert.That(count, Is.EqualTo(permIds.Length));
    }

    [Test]
    public async Task T1_F06_04_Nested_Batch_RemoveRolePermissions_Succeeds_And_Persists_Atomically()
    {
        var customRoleId = Guid.NewGuid();
        Guid[] permIds = [];

        await _host.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.Roles.Add(new Role
            {
                Id = customRoleId,
                Name = $"E2E-RemoveRole-{Guid.NewGuid():N}",
                Type = RoleType.Custom,
                IsSystem = false,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            permIds = await db.Permissions.AsNoTracking().Take(2).Select(p => p.Id).ToArrayAsync();
            foreach (var pid in permIds)
                db.RolePermissions.Add(new RolePermission { RoleId = customRoleId, PermissionId = pid });
            await db.SaveChangesAsync();
        });

        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/roles/{customRoleId}/permissions/batch")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { permissionIds = permIds }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.That(doc.RootElement.GetProperty("removed").GetInt32(), Is.EqualTo(permIds.Length));

        var count = await _host.WithDbAsync(async db =>
            await db.RolePermissions.AsNoTracking().CountAsync(rp => rp.RoleId == customRoleId));
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public async Task T1_F06_05_CreateTicket_With_Invalid_WorkflowState_Fails_Without_Persisting_Ticket()
    {
        var beforeCount = await _host.WithDbAsync(async db => await db.Tickets.CountAsync());

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tickets")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                clientId = _world.ClientA.Id,
                siteId = _world.SiteA1.Id,
                title = "Should Not Persist",
                description = "Invalid workflow state test",
                workflowStateId = Guid.NewGuid(),
                priority = 1
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That((int)resp.StatusCode, Is.InRange(400, 404));

        var afterCount = await _host.WithDbAsync(async db => await db.Tickets.CountAsync());
        Assert.That(afterCount, Is.EqualTo(beforeCount));
    }
}
