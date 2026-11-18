using System.Net;
using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.E2E.Tests.Harness;

namespace Discovery.E2E.Tests.Tier1;

[TestFixture]
public class F02_OutputCacheTests
{
    private ApiE2ETestHost _host = null!;
    private E2ETenantWorld _world = null!;

    [SetUp]
    public async Task SetUp()
    {
        _host = await ApiE2ETestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Security:Cors:AllowWildcardInDevelopment"] = "false"
        });
        _world = await E2ESeedHelper.SeedMultiTenantWorldAsync(_host);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_host is not null)
            await _host.DisposeAsync();
    }

    [Test]
    public async Task T1_F02_01_Unauthenticated_Request_After_Admin_Hit_Returns_401_Not_Cached_200()
    {
        using var adminReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients?includeInactive=false")
            .WithJwt(_world.GlobalAdmin.Jwt);
        using var adminResp = await _host.HttpClient.SendAsync(adminReq);
        Assert.That(adminResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var anonReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients?includeInactive=false");
        using var anonResp = await _host.HttpClient.SendAsync(anonReq);
        Assert.That(anonResp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task T1_F02_02_GlobalJwt_And_ScopedJwt_Receive_Isolated_Client_Lists()
    {
        using var adminReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients?includeInactive=false")
            .WithJwt(_world.GlobalAdmin.Jwt);
        using var adminResp = await _host.HttpClient.SendAsync(adminReq);
        Assert.That(adminResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var adminIds = await ReadClientIdsAsync(adminResp);
        Assert.That(adminIds, Does.Contain(_world.ClientA.Id));
        Assert.That(adminIds, Does.Contain(_world.ClientB.Id));

        using var techAReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients?includeInactive=false")
            .WithJwt(_world.TechA.Jwt);
        using var techAResp = await _host.HttpClient.SendAsync(techAReq);
        Assert.That(techAResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var techAIds = await ReadClientIdsAsync(techAResp);
        Assert.That(techAIds, Is.EquivalentTo(new[] { _world.ClientA.Id }));
    }

    [Test]
    public async Task T1_F02_03_ApiKey_Headers_Vary_Cache_Between_Different_Tenants()
    {
        using var keyAReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients?includeInactive=false")
            .WithApiKey(_world.TechA.ApiKey, _world.TechA.ApiSecret);
        using var keyAResp = await _host.HttpClient.SendAsync(keyAReq);
        Assert.That(keyAResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var keyAIds = await ReadClientIdsAsync(keyAResp);
        Assert.That(keyAIds, Is.EquivalentTo(new[] { _world.ClientA.Id }));

        using var keyBReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients?includeInactive=false")
            .WithApiKey(_world.TechB.ApiKey, _world.TechB.ApiSecret);
        using var keyBResp = await _host.HttpClient.SendAsync(keyBReq);
        Assert.That(keyBResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var keyBIds = await ReadClientIdsAsync(keyBResp);
        Assert.That(keyBIds, Is.EquivalentTo(new[] { _world.ClientB.Id }));
    }

    [Test]
    public async Task T1_F02_04_Cors_Headers_Preserved_On_Cache_Miss_And_Cache_Hit()
    {
        for (var i = 0; i < 2; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients?includeInactive=false")
                .WithApiKey(_world.GlobalAdmin.ApiKey, _world.GlobalAdmin.ApiSecret);
            req.Headers.Add("Origin", "http://localhost:5173");

            using var resp = await _host.HttpClient.SendAsync(req);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins), Is.True);
            Assert.That(origins, Does.Contain("http://localhost:5173"));
        }
    }

    [Test]
    public async Task T1_F02_05_OutputCache_Serves_Cached_Response_Within_Ttl_For_Same_ApiKey()
    {
        using var firstReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients?includeInactive=false")
            .WithApiKey(_world.GlobalAdmin.ApiKey, _world.GlobalAdmin.ApiSecret);
        using var firstResp = await _host.HttpClient.SendAsync(firstReq);
        Assert.That(firstResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var firstIds = await ReadClientIdsAsync(firstResp);
        Assert.That(firstIds, Has.Count.EqualTo(2));

        var newClientId = Guid.NewGuid();
        await _host.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.Clients.Add(new Client { Id = newClientId, Name = "Direct DB Client", IsActive = true, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        });

        using var secondReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients?includeInactive=false")
            .WithApiKey(_world.GlobalAdmin.ApiKey, _world.GlobalAdmin.ApiSecret);
        using var secondResp = await _host.HttpClient.SendAsync(secondReq);
        Assert.That(secondResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var secondIds = await ReadClientIdsAsync(secondResp);
        Assert.That(secondIds, Does.Not.Contain(newClientId));
    }

    private static async Task<List<Guid>> ReadClientIdsAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.GetProperty("items");

        return arr.EnumerateArray()
            .Select(el => el.GetProperty("id").GetGuid())
            .ToList();
    }
}
