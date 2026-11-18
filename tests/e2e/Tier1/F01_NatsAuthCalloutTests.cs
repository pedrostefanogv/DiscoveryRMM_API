using System.Net;
using System.Text;
using System.Text.Json;
using Discovery.Api.Services;
using Discovery.E2E.Tests.Harness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.NKeys;

namespace Discovery.E2E.Tests.Tier1;

[TestFixture]
public class F01_NatsAuthCalloutTests
{
    private ApiE2ETestHost _host = null!;
    private E2ETenantWorld _world = null!;
    private FakeNatsTcpServer _natsServer = null!;
    private KeyPair _accountKeyPair = null!;
    private KeyPair _serverKeyPair = null!;
    private KeyPair _serviceXKeyPair = null!;
    private NatsConnection _serviceNatsConn = null!;

    [SetUp]
    public async Task SetUp()
    {
        _natsServer = new FakeNatsTcpServer();
        _accountKeyPair = KeyPair.CreatePair(PrefixByte.Account);
        _serverKeyPair = KeyPair.CreatePair(PrefixByte.Server);
        _serviceXKeyPair = KeyPair.CreatePair(PrefixByte.Curve);

        _host = await ApiE2ETestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Nats:Url"] = _natsServer.Url,
            ["Nats:AccountSeed"] = _accountKeyPair.GetSeed(),
            ["Nats:ServerPublicKey"] = _serverKeyPair.GetPublicKey()
        });
        _world = await E2ESeedHelper.SeedMultiTenantWorldAsync(_host);

        _serviceNatsConn = new NatsConnection(new NatsOpts { Url = _natsServer.Url });
        await _serviceNatsConn.ConnectAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceNatsConn is not null)
            await _serviceNatsConn.DisposeAsync();
        _accountKeyPair?.Dispose();
        _serverKeyPair?.Dispose();
        _serviceXKeyPair?.Dispose();
        if (_host is not null)
            await _host.DisposeAsync();
        if (_natsServer is not null)
            await _natsServer.DisposeAsync();
    }

    private NatsAuthCalloutBackgroundService CreateCalloutService(string? xKeySeed = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nats:Url"] = _natsServer.Url,
                ["Nats:AccountSeed"] = _accountKeyPair.GetSeed(),
                ["Nats:ServerPublicKey"] = _serverKeyPair.GetPublicKey(),
                ["Nats:XKeySeed"] = xKeySeed ?? string.Empty,
                ["Nats:AuthCallout:Enabled"] = "true",
                ["Nats:AuthCallout:Subject"] = "$SYS.REQ.USER.AUTH",
                ["Nats:AuthCallout:Account"] = "$G"
            })
            .Build();

        return new NatsAuthCalloutBackgroundService(
            _serviceNatsConn,
            config,
            _host.Services.GetRequiredService<IServiceScopeFactory>(),
            new NatsAuthCalloutReloadSignal(),
            NullLogger<NatsAuthCalloutBackgroundService>.Instance);
    }

    [Test]
    public async Task T1_F01_01_Valid_ServerRequest_And_Valid_PreIssued_UserJwt_Issues_Session_Jwt()
    {
        using var svc = CreateCalloutService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await _natsServer.WaitForSubscriptionAsync("$SYS.REQ.USER.AUTH");

        using var origUserKey = KeyPair.CreatePair(PrefixByte.User);
        using var ephUserKey = KeyPair.CreatePair(PrefixByte.User);
        var preIssuedJwt = NatsAuthCalloutTestHelper.BuildPreIssuedUserJwt(
            _accountKeyPair,
            origUserKey.GetPublicKey(),
            ["tenant.a.proxy.req"],
            ["tenant.a.proxy.resp"]);

        var authReqJwt = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(
            _serverKeyPair,
            ephUserKey.GetPublicKey(),
            authToken: preIssuedJwt);

        var reply = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(authReqJwt));
        var decoded = NatsAuthCalloutTestHelper.DecodeAuthResponse(reply.PayloadText, _accountKeyPair);

        Assert.That(decoded.Error, Is.Null);
        Assert.That(decoded.Jwt, Is.Not.Null.And.Not.Empty);

        var (sub, iss, pub, subAllow) = NatsAuthCalloutTestHelper.DecodeUserJwt(decoded.Jwt!, _accountKeyPair);
        Assert.That(sub, Is.EqualTo(ephUserKey.GetPublicKey()));
        Assert.That(iss, Is.EqualTo(_accountKeyPair.GetPublicKey()));
        Assert.That(pub, Does.Contain("tenant.a.proxy.req"));
        Assert.That(subAllow, Does.Contain("tenant.a.proxy.resp"));

        await svc.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task T1_F01_02_Valid_Agent_MdzToken_Issues_Scoped_Agent_Nats_Jwt()
    {
        using var svc = CreateCalloutService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await _natsServer.WaitForSubscriptionAsync("$SYS.REQ.USER.AUTH");

        using var ephUserKey = KeyPair.CreatePair(PrefixByte.User);
        var authReqJwt = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(
            _serverKeyPair,
            ephUserKey.GetPublicKey(),
            authToken: _world.AgentTokenA1);

        var reply = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(authReqJwt));
        var decoded = NatsAuthCalloutTestHelper.DecodeAuthResponse(reply.PayloadText, _accountKeyPair);

        Assert.That(decoded.Error, Is.Null);
        Assert.That(decoded.Jwt, Is.Not.Null.And.Not.Empty);

        var (sub, _, pub, _) = NatsAuthCalloutTestHelper.DecodeUserJwt(decoded.Jwt!, _accountKeyPair);
        Assert.That(sub, Is.EqualTo(ephUserKey.GetPublicKey()));
        Assert.That(pub, Does.Contain($"tenant.{_world.ClientA.Id}.site.{_world.SiteA1.Id}.agent.{_world.AgentA1.Id}.heartbeat"));

        await svc.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task T1_F01_03_Valid_User_Rs256_Jwt_With_Dashboard_Permission_Issues_User_Nats_Jwt()
    {
        using var svc = CreateCalloutService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await _natsServer.WaitForSubscriptionAsync("$SYS.REQ.USER.AUTH");

        using var ephUserKey = KeyPair.CreatePair(PrefixByte.User);
        var authReqJwt = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(
            _serverKeyPair,
            ephUserKey.GetPublicKey(),
            authToken: _world.TechA.Jwt);

        var reply = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(authReqJwt));
        var decoded = NatsAuthCalloutTestHelper.DecodeAuthResponse(reply.PayloadText, _accountKeyPair);

        Assert.That(decoded.Error, Is.Null);
        Assert.That(decoded.Jwt, Is.Not.Null.And.Not.Empty);

        var (sub, _, _, subAllow) = NatsAuthCalloutTestHelper.DecodeUserJwt(decoded.Jwt!, _accountKeyPair);
        Assert.That(sub, Is.EqualTo(ephUserKey.GetPublicKey()));
        Assert.That(subAllow.Any(s => s.Contains(_world.ClientA.Id.ToString(), StringComparison.OrdinalIgnoreCase)), Is.True);

        await svc.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task T1_F01_04_XKey_Encrypted_Auth_Request_Decrypts_And_Seals_Response()
    {
        using var svc = CreateCalloutService(xKeySeed: _serviceXKeyPair.GetSeed());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await _natsServer.WaitForSubscriptionAsync("$SYS.REQ.USER.AUTH");

        using var serverEphemeralXKey = KeyPair.CreatePair(PrefixByte.Curve);
        using var origUserKey = KeyPair.CreatePair(PrefixByte.User);
        using var ephUserKey = KeyPair.CreatePair(PrefixByte.User);

        var preIssuedJwt = NatsAuthCalloutTestHelper.BuildPreIssuedUserJwt(
            _accountKeyPair,
            origUserKey.GetPublicKey(),
            ["tenant.xkey.pub"],
            ["tenant.xkey.sub"]);

        var authReqJwt = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(
            _serverKeyPair,
            ephUserKey.GetPublicKey(),
            authToken: preIssuedJwt);

        var sealedRequest = serverEphemeralXKey.Seal(
            Encoding.UTF8.GetBytes(authReqJwt),
            _serviceXKeyPair.GetPublicKey());

        var reply = await _natsServer.RequestAsync(
            "$SYS.REQ.USER.AUTH",
            sealedRequest,
            new Dictionary<string, string> { ["Nats-Server-Xkey"] = serverEphemeralXKey.GetPublicKey() });

        var opened = serverEphemeralXKey.Open(reply.Payload, _serviceXKeyPair.GetPublicKey());
        var responseJwt = Encoding.UTF8.GetString(opened);
        var decoded = NatsAuthCalloutTestHelper.DecodeAuthResponse(responseJwt, _accountKeyPair);

        Assert.That(decoded.Error, Is.Null);
        Assert.That(decoded.Jwt, Is.Not.Null.And.Not.Empty);

        await svc.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task T1_F01_05_Get_NatsAuth_Status_Endpoint_Returns_Ok_For_Authenticated_User()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/nats-auth")
            .WithJwt(_world.GlobalAdmin.Jwt);
        using var resp = await _host.HttpClient.SendAsync(req);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.That(doc.RootElement.TryGetProperty("connected", out _), Is.True);
    }
}
