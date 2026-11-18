using System.Net;
using System.Text;
using System.Text.Json;
using Discovery.E2E.Tests.Harness;
using Microsoft.EntityFrameworkCore;

namespace Discovery.E2E.Tests.Tier2;

[TestFixture]
public class F07_AgentAuthEdgeTests
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
    public async Task T2_F07_01_Missing_Invalid_And_Mismatched_XAgentId_Return_Expected_4xx()
    {
        // Missing X-Agent-ID -> 401
        using var missingReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me")
            .WithAgentAuth(_world.AgentTokenA1, null);
        using var missingResp = await _host.HttpClient.SendAsync(missingReq);
        Assert.That(missingResp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        // Non-GUID X-Agent-ID -> 400
        using var invalidReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me");
        invalidReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _world.AgentTokenA1);
        invalidReq.Headers.Add("X-Agent-ID", "not-a-guid");
        using var invalidResp = await _host.HttpClient.SendAsync(invalidReq);
        Assert.That(invalidResp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Mismatched X-Agent-ID (AgentB1's ID with AgentA1's token) -> 401
        using var mismatchReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me")
            .WithAgentAuth(_world.AgentTokenA1, _world.AgentB1.Id);
        using var mismatchResp = await _host.HttpClient.SendAsync(mismatchReq);
        Assert.That(mismatchResp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task T2_F07_02_AgentTools_Registry_Is_Exempt_From_XAgentId_Header()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent-auth/me/agent-tools/registry")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                tools = Array.Empty<object>()
            }), Encoding.UTF8, "application/json")
        }.WithAgentAuth(_world.AgentTokenA1, null);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That((int)resp.StatusCode, Is.Not.EqualTo(401).And.Not.EqualTo(400));
    }

    [Test]
    public async Task T2_F07_03_Trashed_Agent_Is_Blocked_With_403_On_Inventory_While_Decommission_Path_Bypass_Applies()
    {
        var (_, trashedToken) = await _host.WithDbAsync(async db =>
            await E2ESeedHelper.CreateAgentWithTokenAsync(db, _world.SiteA1.Id, "TRASHED-EDGE", deletedAt: DateTime.UtcNow));

        var trashedId = await _host.WithDbAsync(async db =>
            db.Agents.IgnoreQueryFilters().First(a => a.Hostname == "TRASHED-EDGE").Id);

        using var hwReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me/hardware")
            .WithAgentAuth(trashedToken, trashedId);
        using var hwResp = await _host.HttpClient.SendAsync(hwReq);
        Assert.That(hwResp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using var rebootReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent-auth/me/pending-reboot")
        {
            Content = new StringContent("{\"pendingReboot\":true}", Encoding.UTF8, "application/json")
        }.WithAgentAuth(trashedToken, trashedId);
        using var rebootResp = await _host.HttpClient.SendAsync(rebootReq);
        Assert.That(rebootResp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        // /agent-auth/me is the sole decommission exemption in AgentAuthMiddleware (does not return 403 middleware block)
        using var meReq = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/agent-auth/me")
            .WithAgentAuth(trashedToken, trashedId);
        using var meResp = await _host.HttpClient.SendAsync(meReq);
        Assert.That(meResp.StatusCode, Is.Not.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task T2_F07_04_Invalid_Mdz_Token_Returns_401()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me")
            .WithAgentAuth("mdz_invalid_nonexistent_token", _world.AgentA1.Id);
        using var resp = await _host.HttpClient.SendAsync(req);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task T2_F07_05_Missing_Authorization_Header_On_AgentAuth_Returns_401()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me");
        req.Headers.Add("X-Agent-ID", _world.AgentA1.Id.ToString());
        using var resp = await _host.HttpClient.SendAsync(req);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
