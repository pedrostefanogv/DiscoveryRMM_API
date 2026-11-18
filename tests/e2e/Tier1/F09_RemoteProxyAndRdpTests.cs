using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Discovery.E2E.Tests.Harness;
using NATS.NKeys;

namespace Discovery.E2E.Tests.Tier1;

[TestFixture]
public class F09_RemoteProxyAndRdpTests
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
    public async Task T1_F09_01_Start_Proxy_Session_Returns_Active_With_Proxy_Nats_Permissions()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{_world.AgentA1.Id}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                kind = "proxy",
                transport = "nats"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That((int)resp.StatusCode, Is.InRange(200, 201));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.That(root.GetProperty("status").GetString(), Is.EqualTo("active"));

        var jwt = root.GetProperty("viewerCredentials").GetProperty("jwt").GetString()!;
        var (_, _, pubAllow, subAllow) = NatsAuthCalloutTestHelper.DecodeUserJwt(jwt, _accountKeyPair);

        Assert.That(pubAllow.Any(p => p.Contains(".proxy.req", StringComparison.Ordinal)), Is.True);
        Assert.That(subAllow.Any(s => s.Contains(".proxy.resp", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task T1_F09_02_Start_Rdp_Session_Returns_Active_With_Tunnel_And_Rdp_Permissions()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{_world.AgentA1.Id}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                kind = "rdp",
                transport = "nats"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That((int)resp.StatusCode, Is.InRange(200, 201));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.That(root.GetProperty("status").GetString(), Is.EqualTo("active"));

        var jwt = root.GetProperty("viewerCredentials").GetProperty("jwt").GetString()!;
        var (_, _, pubAllow, subAllow) = NatsAuthCalloutTestHelper.DecodeUserJwt(jwt, _accountKeyPair);

        Assert.That(pubAllow.Any(p => p.Contains(".tunnel.in", StringComparison.Ordinal) || p.Contains(".rdp.", StringComparison.Ordinal)), Is.True);
        Assert.That(subAllow.Any(s => s.Contains(".tunnel.out", StringComparison.Ordinal) || s.Contains(".rdp.", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task T1_F09_03_Start_Proxy_Session_Dispatches_Proxy_Policy_To_Agent()
    {
        _host.FakeAgentMessaging.Clear();

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{_world.AgentA1.Id}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                kind = "proxy",
                transport = "nats"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That((int)resp.StatusCode, Is.InRange(200, 201));

        // NOTA (ajuste de spec): o CommandType gravado no IAgentMessaging é o
        // valor de WIRE do contrato agente (CommandTypeWireMapper.ToWireValue ->
        // "remotesessionstart", tudo minúsculo). A comparação original era
        // case-sensitive contra "RemoteSessionStart" e nunca casava — a
        // comparação abaixo é a mesma checagem, insensível a caixa.
        var sent = _host.FakeAgentMessaging.SentCommands
            .FirstOrDefault(c => string.Equals(c.CommandType, "RemoteSessionStart", StringComparison.OrdinalIgnoreCase));
        Assert.That(sent, Is.Not.Null);
        Assert.That(sent!.Payload, Does.Contain("proxy").IgnoreCase);
        Assert.That(sent.Payload, Does.Contain("allowedPorts").IgnoreCase);
    }

    [Test]
    public async Task T1_F09_04_Get_RdpFile_Returns_Downloadable_Rdp_Pointing_To_Loopback_Port()
    {
        using var startReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{_world.AgentA1.Id}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                kind = "rdp",
                transport = "nats"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.TechA.Jwt);

        using var startResp = await _host.HttpClient.SendAsync(startReq);
        Assert.That((int)startResp.StatusCode, Is.InRange(200, 201));
        using var startDoc = JsonDocument.Parse(await startResp.Content.ReadAsStringAsync());
        var sessionId = startDoc.RootElement.GetProperty("sessionId").GetGuid();

        using var rdpReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/remote-sessions/{_world.AgentA1.Id}/{sessionId}/rdp-file?localPort=33890")
            .WithJwt(_world.TechA.Jwt);

        using var rdpResp = await _host.HttpClient.SendAsync(rdpReq);
        Assert.That(rdpResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var rdpText = await rdpResp.Content.ReadAsStringAsync();
        Assert.That(rdpText, Does.Contain("full address:s:127.0.0.1:33890"));
    }

    [Test]
    public async Task T1_F09_05_Tunnel_WebSocket_Relays_Frames_Between_Client_And_Nats()
    {
        await using var natsServer = new FakeNatsTcpServer();
        using var wsAccountKey = KeyPair.CreatePair(PrefixByte.Account);
        await using var wsHost = await ApiE2ETestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["Nats:Url"] = natsServer.Url,
                ["Nats:AccountSeed"] = wsAccountKey.GetSeed()
            },
            useFakeAgentMessaging: false);
        var wsWorld = await E2ESeedHelper.SeedMultiTenantWorldAsync(wsHost);

        using var startReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/remote-sessions/{wsWorld.AgentA1.Id}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                kind = "rdp",
                transport = "nats"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(wsWorld.TechA.Jwt);

        using var startResp = await wsHost.HttpClient.SendAsync(startReq);
        Assert.That((int)startResp.StatusCode, Is.InRange(200, 201));
        using var startDoc = JsonDocument.Parse(await startResp.Content.ReadAsStringAsync());
        var sessionId = startDoc.RootElement.GetProperty("sessionId").GetGuid();
        var natsSubject = startDoc.RootElement.GetProperty("natsSubject").GetString()!;

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {wsWorld.TechA.Jwt}");
        var wsUri = new Uri($"ws://127.0.0.1:{wsHost.BaseAddress.Port}/api/v1/remote-sessions/{wsWorld.AgentA1.Id}/{sessionId}/tunnel/ws");
        await ws.ConnectAsync(wsUri, CancellationToken.None);

        var openFrame = Encoding.UTF8.GetBytes("{\"streamId\":1,\"op\":\"open\",\"protocol\":\"rdp\",\"targetPort\":3389}");
        await ws.SendAsync(openFrame, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        var published = await natsServer.WaitForPublishedAsync(m => m.Subject == $"{natsSubject}.tunnel.in");
        Assert.That(published.PayloadText, Does.Contain("open"));

        var replyPayload = Encoding.UTF8.GetBytes("{\"streamId\":1,\"op\":\"ready\"}");
        await natsServer.PublishAsync($"{natsSubject}.tunnel.out", replyPayload);

        var recvBuf = new byte[4096];
        using var recvCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var recv = await ws.ReceiveAsync(recvBuf, recvCts.Token);
        var recvText = Encoding.UTF8.GetString(recvBuf, 0, recv.Count);
        Assert.That(recvText, Does.Contain("ready"));

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}
