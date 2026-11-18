using System.Net;
using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.E2E.Tests.Harness;

namespace Discovery.E2E.Tests.Tier2;

[TestFixture]
public class F02_OutputCacheEdgeTests
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
    public async Task T2_F02_01_Missing_ApiSecret_Returns_401_Even_After_Valid_ApiKey_Cache_Warm()
    {
        using var validReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients")
            .WithApiKey(_world.GlobalAdmin.ApiKey, _world.GlobalAdmin.ApiSecret);
        using var validResp = await _host.HttpClient.SendAsync(validReq);
        Assert.That(validResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var missingSecretReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients");
        missingSecretReq.Headers.Add("X-Api-Key", _world.GlobalAdmin.ApiKey);
        using var missingSecretResp = await _host.HttpClient.SendAsync(missingSecretReq);
        Assert.That(missingSecretResp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task T2_F02_02_Wrong_ApiSecret_Returns_401_Never_Cached_Payload()
    {
        using var validReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients")
            .WithApiKey(_world.GlobalAdmin.ApiKey, _world.GlobalAdmin.ApiSecret);
        using var validResp = await _host.HttpClient.SendAsync(validReq);
        Assert.That(validResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var wrongSecretReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients")
            .WithApiKey(_world.GlobalAdmin.ApiKey, "mzk_wrong_secret_value");
        using var wrongSecretResp = await _host.HttpClient.SendAsync(wrongSecretReq);
        Assert.That(wrongSecretResp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task T2_F02_03_QueryString_IncludeInactive_Varies_Cache_Entries()
    {
        var inactiveId = Guid.NewGuid();
        await _host.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.Clients.Add(new Client { Id = inactiveId, Name = "Inactive Client", IsActive = false, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        });

        using var activeOnlyReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients?includeInactive=false")
            .WithApiKey(_world.GlobalAdmin.ApiKey, _world.GlobalAdmin.ApiSecret);
        using var activeOnlyResp = await _host.HttpClient.SendAsync(activeOnlyReq);
        Assert.That(activeOnlyResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var activeOnlyBody = await activeOnlyResp.Content.ReadAsStringAsync();
        Assert.That(activeOnlyBody, Does.Not.Contain(inactiveId.ToString()));

        using var includeInactiveReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients?includeInactive=true")
            .WithApiKey(_world.GlobalAdmin.ApiKey, _world.GlobalAdmin.ApiSecret);
        using var includeInactiveResp = await _host.HttpClient.SendAsync(includeInactiveReq);
        Assert.That(includeInactiveResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var includeInactiveBody = await includeInactiveResp.Content.ReadAsStringAsync();
        Assert.That(includeInactiveBody, Does.Contain(inactiveId.ToString()));
    }

    [Test]
    public async Task T2_F02_04_Authorization_Bearer_Does_Not_Serve_Cross_User_Cached_Responses()
    {
        using var adminReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients")
            .WithJwt(_world.GlobalAdmin.Jwt);
        using var adminResp = await _host.HttpClient.SendAsync(adminReq);
        Assert.That(adminResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var techBReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients")
            .WithJwt(_world.TechB.Jwt);
        using var techBResp = await _host.HttpClient.SendAsync(techBReq);
        Assert.That(techBResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var techBBody = await techBResp.Content.ReadAsStringAsync();
        Assert.That(techBBody, Does.Contain(_world.ClientB.Id.ToString()));
        Assert.That(techBBody, Does.Not.Contain(_world.ClientA.Id.ToString()));
    }

    [Test]
    public async Task T2_F02_05_Non200_Unauthorized_Response_Is_Never_Cached_For_Subsequent_Authorized_Call()
    {
        using var badReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients")
            .WithApiKey(_world.GlobalAdmin.ApiKey, "mzk_invalid");
        using var badResp = await _host.HttpClient.SendAsync(badReq);
        Assert.That(badResp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        using var goodReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients")
            .WithApiKey(_world.GlobalAdmin.ApiKey, _world.GlobalAdmin.ApiSecret);
        using var goodResp = await _host.HttpClient.SendAsync(goodReq);
        Assert.That(goodResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var doc = JsonDocument.Parse(await goodResp.Content.ReadAsStringAsync());
        Assert.That(doc.RootElement.GetArrayLength(), Is.GreaterThanOrEqualTo(2));
    }
}
