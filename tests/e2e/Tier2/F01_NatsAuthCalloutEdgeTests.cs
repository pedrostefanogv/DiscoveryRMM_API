using System.Text;
using System.Text.Json;
using Discovery.Api.Services;
using Discovery.E2E.Tests.Harness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.NKeys;

namespace Discovery.E2E.Tests.Tier2;

[TestFixture]
public class F01_NatsAuthCalloutEdgeTests
{
    private ApiE2ETestHost _host = null!;
    private E2ETenantWorld _world = null!;
    private FakeNatsTcpServer _natsServer = null!;
    private KeyPair _accountKeyPair = null!;
    private KeyPair _serverKeyPair = null!;
    private NatsConnection _serviceNatsConn = null!;

    [SetUp]
    public async Task SetUp()
    {
        _natsServer = new FakeNatsTcpServer();
        _accountKeyPair = KeyPair.CreatePair(PrefixByte.Account);
        _serverKeyPair = KeyPair.CreatePair(PrefixByte.Server);

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
        await _serviceNatsConn.DisposeAsync();
        _accountKeyPair.Dispose();
        _serverKeyPair.Dispose();
        await _host.DisposeAsync();
        await _natsServer.DisposeAsync();
    }

    private NatsAuthCalloutBackgroundService CreateCalloutService(string? serverPublicKey = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nats:Url"] = _natsServer.Url,
                ["Nats:AccountSeed"] = _accountKeyPair.GetSeed(),
                ["Nats:ServerPublicKey"] = serverPublicKey ?? _serverKeyPair.GetPublicKey(),
                ["Nats:AuthCallout:Enabled"] = "true",
                ["Nats:AuthCallout:Subject"] = "$SYS.REQ.USER.AUTH"
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
    public async Task T2_F01_01_Forged_PreIssued_UserJwt_With_Tampered_Payload_Is_Rejected()
    {
        using var svc = CreateCalloutService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await _natsServer.WaitForSubscriptionAsync("$SYS.REQ.USER.AUTH");

        using var origUserKey = KeyPair.CreatePair(PrefixByte.User);
        using var ephUserKey = KeyPair.CreatePair(PrefixByte.User);
        var validJwt = NatsAuthCalloutTestHelper.BuildPreIssuedUserJwt(
            _accountKeyPair,
            origUserKey.GetPublicKey(),
            ["tenant.a.safe"],
            ["tenant.a.safe"]);

        // Forge payload without re-signing
        var parts = validJwt.Split('.');
        var forgedPayloadJson = Encoding.UTF8.GetString(NatsAuthCalloutTestHelper.Base64UrlDecode(parts[1]))
            .Replace("tenant.a.safe", ">", StringComparison.Ordinal);
        var forgedJwt = $"{parts[0]}.{NatsAuthCalloutTestHelper.Base64UrlEncode(Encoding.UTF8.GetBytes(forgedPayloadJson))}.{parts[2]}";

        var authReqJwt = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(
            _serverKeyPair,
            ephUserKey.GetPublicKey(),
            authToken: forgedJwt);

        var reply = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(authReqJwt));
        var decoded = NatsAuthCalloutTestHelper.DecodeAuthResponse(reply.PayloadText, _accountKeyPair);

        Assert.That(decoded.Jwt, Is.Null);
        Assert.That(decoded.Error, Is.Not.Null.And.Not.Empty);

        await svc.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task T2_F01_02_Tampered_Server_AuthRequest_Signature_Is_Rejected()
    {
        using var svc = CreateCalloutService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await _natsServer.WaitForSubscriptionAsync("$SYS.REQ.USER.AUTH");

        using var ephUserKey = KeyPair.CreatePair(PrefixByte.User);
        var validReqJwt = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(
            _serverKeyPair,
            ephUserKey.GetPublicKey(),
            authToken: _world.AgentTokenA1);

        var parts = validReqJwt.Split('.');
        var badSig = NatsAuthCalloutTestHelper.Base64UrlDecode(parts[2]);
        badSig[0] ^= 0xFF;
        var tamperedReqJwt = $"{parts[0]}.{parts[1]}.{NatsAuthCalloutTestHelper.Base64UrlEncode(badSig)}";

        var reply = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(tamperedReqJwt));
        var decoded = NatsAuthCalloutTestHelper.DecodeAuthResponse(reply.PayloadText, _accountKeyPair);

        Assert.That(decoded.Jwt, Is.Null);
        Assert.That(decoded.Error, Does.Contain("Invalid auth request"));

        await svc.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task T2_F01_03_Untrusted_Server_Key_When_ServerPublicKey_Configured_Is_Rejected()
    {
        using var rogueServerKey = KeyPair.CreatePair(PrefixByte.Server);
        using var svc = CreateCalloutService(serverPublicKey: _serverKeyPair.GetPublicKey());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await _natsServer.WaitForSubscriptionAsync("$SYS.REQ.USER.AUTH");

        using var ephUserKey = KeyPair.CreatePair(PrefixByte.User);
        var rogueReqJwt = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(
            rogueServerKey,
            ephUserKey.GetPublicKey(),
            authToken: _world.AgentTokenA1);

        var reply = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(rogueReqJwt));
        var decoded = NatsAuthCalloutTestHelper.DecodeAuthResponse(reply.PayloadText, _accountKeyPair);

        Assert.That(decoded.Jwt, Is.Null);
        Assert.That(decoded.Error, Does.Contain("Invalid auth request"));

        await svc.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task T2_F01_04_Expired_And_Future_NotBefore_PreIssued_Nats_Jwts_Are_Rejected()
    {
        using var svc = CreateCalloutService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await _natsServer.WaitForSubscriptionAsync("$SYS.REQ.USER.AUTH");

        using var origUserKey = KeyPair.CreatePair(PrefixByte.User);
        using var ephUserKey = KeyPair.CreatePair(PrefixByte.User);

        var expiredJwt = NatsAuthCalloutTestHelper.BuildPreIssuedUserJwt(
            _accountKeyPair,
            origUserKey.GetPublicKey(),
            ["tenant.a.req"],
            ["tenant.a.resp"],
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var expiredReq = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(_serverKeyPair, ephUserKey.GetPublicKey(), authToken: expiredJwt);
        var expiredReply = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(expiredReq));
        var decodedExpired = NatsAuthCalloutTestHelper.DecodeAuthResponse(expiredReply.PayloadText, _accountKeyPair);
        Assert.That(decodedExpired.Jwt, Is.Null);

        var futureNbfJwt = NatsAuthCalloutTestHelper.BuildPreIssuedUserJwt(
            _accountKeyPair,
            origUserKey.GetPublicKey(),
            ["tenant.a.req"],
            ["tenant.a.resp"],
            expiresAt: DateTimeOffset.UtcNow.AddHours(2),
            notBefore: DateTimeOffset.UtcNow.AddMinutes(10));

        var futureReq = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(_serverKeyPair, ephUserKey.GetPublicKey(), authToken: futureNbfJwt);
        var futureReply = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(futureReq));
        var decodedFuture = NatsAuthCalloutTestHelper.DecodeAuthResponse(futureReply.PayloadText, _accountKeyPair);
        Assert.That(decodedFuture.Jwt, Is.Null);

        await svc.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task T2_F01_05_Duplicate_MdzToken_Connection_Is_Rejected_When_Redis_Session_Active()
    {
        using var svc = CreateCalloutService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await _natsServer.WaitForSubscriptionAsync("$SYS.REQ.USER.AUTH");

        using var eph1 = KeyPair.CreatePair(PrefixByte.User);
        var req1 = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(_serverKeyPair, eph1.GetPublicKey(), authToken: _world.AgentTokenA1);
        var reply1 = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(req1));
        var decoded1 = NatsAuthCalloutTestHelper.DecodeAuthResponse(reply1.PayloadText, _accountKeyPair);
        Assert.That(decoded1.Jwt, Is.Not.Null);

        // Second distinct connection with DIFFERENT ephemeral user_nkey reusing same mdz_ token
        using var eph2 = KeyPair.CreatePair(PrefixByte.User);
        var req2 = NatsAuthCalloutTestHelper.BuildSignedAuthRequestJwt(_serverKeyPair, eph2.GetPublicKey(), authToken: _world.AgentTokenA1);
        var reply2 = await _natsServer.RequestAsync("$SYS.REQ.USER.AUTH", Encoding.UTF8.GetBytes(req2));
        var decoded2 = NatsAuthCalloutTestHelper.DecodeAuthResponse(reply2.PayloadText, _accountKeyPair);

        Assert.That(decoded2.Jwt, Is.Null);
        Assert.That(decoded2.Error, Does.Contain("already in use"));

        await svc.StopAsync(CancellationToken.None);
    }
}
