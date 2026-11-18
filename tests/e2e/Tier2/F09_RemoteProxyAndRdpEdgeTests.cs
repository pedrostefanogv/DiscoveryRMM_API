using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Discovery.E2E.Tests.Harness;
using NATS.NKeys;

namespace Discovery.E2E.Tests.Tier2;

[TestFixture]
public class F09_RemoteProxyAndRdpEdgeTests
{
    private ApiE2ETestHost _host = null!;
    private E2ETenantWorld _world = null!;
    private KeyPair _accountKeyPair = null!;

    [SetUp]
    public async Task SetUp()
    {
        _accountKeyPair = KeyPair.CreatePair(PrefixByte.Account);
        _host = await ApiE2ETestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Nats:AccountSeed"] = _accountKeyPair.GetSeed()
        });
        _world = await E2ESeedHelper.SeedMultiTenantWorldAsync(_host);
    }

    [TearDown]
    public async Task TearDown()
    {
        _accountKeyPair?.Dispose();
        if (_host is not null)
            await _host.DisposeAsync();
    }

    [Test]
    public async Task T2_F09_01_Session_With_NoProxy_Right_Bit64_Excludes_Proxy_Nats_Permissions()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{_world.AgentA1.Id}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                kind = "all",
                transport = "nats",
                rights = 64 // RemoteSessionRights.NoProxy
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That((int)resp.StatusCode, Is.InRange(200, 201));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("viewerCredentials", out var creds) && creds.ValueKind == JsonValueKind.Object)
        {
            var jwt = creds.GetProperty("jwt").GetString()!;
            var (_, _, pubAllow, subAllow) = NatsAuthCalloutTestHelper.DecodeUserJwt(jwt, _accountKeyPair);
            Assert.That(pubAllow.Any(p => p.Contains(".proxy.req", StringComparison.Ordinal)), Is.False);
            Assert.That(subAllow.Any(s => s.Contains(".proxy.resp", StringComparison.Ordinal)), Is.False);
        }
    }

    [Test]
    public async Task T2_F09_02_Session_With_NoRdp_Right_Bit128_Excludes_Rdp_Nats_Permissions()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{_world.AgentA1.Id}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                kind = "all",
                transport = "nats",
                rights = 128 // RemoteSessionRights.NoRdp
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That((int)resp.StatusCode, Is.InRange(200, 201));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("viewerCredentials", out var creds) && creds.ValueKind == JsonValueKind.Object)
        {
            var jwt = creds.GetProperty("jwt").GetString()!;
            var (_, _, pubAllow, subAllow) = NatsAuthCalloutTestHelper.DecodeUserJwt(jwt, _accountKeyPair);
            Assert.That(pubAllow.Any(p => p.Contains(".rdp.", StringComparison.Ordinal)), Is.False);
            Assert.That(subAllow.Any(s => s.Contains(".rdp.", StringComparison.Ordinal)), Is.False);
        }
    }

    [Test]
    public async Task T2_F09_03_Get_RdpFile_Rejects_Invalid_Port_And_CrossTenant_Session()
    {
        using var badPortReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/remote-sessions/{_world.AgentA1.Id}/{_world.RemoteSessionA1.Id}/rdp-file?localPort=70000")
            .WithJwt(_world.TechA.Jwt);
        using var badPortResp = await _host.HttpClient.SendAsync(badPortReq);
        Assert.That(badPortResp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using var crossTenantReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/remote-sessions/{_world.AgentB1.Id}/{_world.RemoteSessionB1.Id}/rdp-file?localPort=33890")
            .WithJwt(_world.TechA.Jwt);
        using var crossTenantResp = await _host.HttpClient.SendAsync(crossTenantReq);
        Assert.That((int)crossTenantResp.StatusCode, Is.AnyOf(403, 404));
    }

    [Test]
    public async Task T2_F09_04_Tunnel_Ws_Rejects_Unauthenticated_And_CrossTenant_Access()
    {
        using var unauthReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/remote-sessions/{_world.AgentA1.Id}/{_world.RemoteSessionA1.Id}/tunnel/ws");
        using var unauthResp = await _host.HttpClient.SendAsync(unauthReq);
        Assert.That(unauthResp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        using var crossReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/remote-sessions/{_world.AgentB1.Id}/{_world.RemoteSessionB1.Id}/tunnel/ws")
            .WithJwt(_world.TechA.Jwt);
        using var crossResp = await _host.HttpClient.SendAsync(crossReq);
        Assert.That((int)crossResp.StatusCode, Is.AnyOf(403, 404));
    }

    [Test]
    public async Task T2_F09_05_Start_Proxy_On_Unknown_Agent_Returns_404_And_Stop_Closes_Active_Session()
    {
        using var unknownReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{Guid.NewGuid()}")
        {
            Content = new StringContent("{\"kind\":\"proxy\",\"transport\":\"nats\"}", Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);
        using var unknownResp = await _host.HttpClient.SendAsync(unknownReq);
        Assert.That(unknownResp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        using var stopReq = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/remote-sessions/{_world.AgentA1.Id}/{_world.RemoteSessionA1.Id}/stop")
            .WithJwt(_world.TechA.Jwt);
        using var stopResp = await _host.HttpClient.SendAsync(stopReq);
        Assert.That(stopResp.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }
}
