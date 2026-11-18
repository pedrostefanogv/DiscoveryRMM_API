using System.Text;
using System.Text.Json;
using Discovery.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Jwt;
using NATS.Jwt.Models;
using NATS.NKeys;

namespace Discovery.Tests;

/// <summary>
/// Regressão do "Authorization Violation" no CONNECT do console de remote debug.
///
/// Duas causas se somavam:
///
/// 1. O nats-server em modo config (auth_callout sem operator mode) DESCARTA o
///    campo "jwt" do CONNECT antes de chamar o auth callout
///    (nats-server client.go: "when not in operator mode, discard the jwt").
///    Por isso a credencial escopada precisa chegar como auth_token — a
///    validação abaixo é o que o callout aplica nesse token.
///
/// 2. Mesmo quando o JWT chegava, o subject NÃO pode ser comparado com o
///    user_nkey do auth request: esse user_nkey é uma chave efêmera gerada pelo
///    NATS por conexão (nats-server auth_callout.go) e o server só aceita o JWT
///    devolvido se o subject dele for exatamente essa chave. O handler sempre
///    reemite o JWT com sub = user_nkey.
///
/// Estes testes cobrem a validação (issuer/exp/perms) usada antes da reemissão.
/// A lib NATS.Jwt 1.0.1 lança NatsJwtException ao decodificar um JWT de usuário
/// gerado por NatsJwt.EncodeUserClaims quando o claim "nats" contém "pub.allow"
/// — exatamente o caso do viewer, que precisa PUBLICAR o ping no canal de
/// controle. A decodificação manual em TryValidatePreIssuedNatsJwt evita isso.
/// </summary>
[TestFixture]
public class NatsAuthCalloutUserJwtTests
{
    private static readonly string TestAccountSeed =
        KeyPair.CreatePair(PrefixByte.Account).GetSeed();

    private const string ControlSubject = "tenant.c.site.s.agent.a.remote-debug.control";
    private const string LogSubject = "tenant.c.site.s.agent.a.remote-debug.log";

    private static NatsAuthCalloutBackgroundService CreateService(string? accountSeed = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nats:AccountSeed"] = accountSeed ?? TestAccountSeed,
            })
            .Build();

        return new NatsAuthCalloutBackgroundService(
            natsConnection: null!,
            configuration: configuration,
            scopeFactory: null!,
            reloadSignal: null!,
            logger: NullLogger<NatsAuthCalloutBackgroundService>.Instance);
    }

    /// <summary>Emite o MESMO JWT que o NatsCredentialsService emite para o viewer.</summary>
    private static string IssueViewerJwt(
        string userNkey,
        string name = "user:regression",
        string accountSeed = "",
        string[]? subscribeSubjects = null,
        string[]? publishSubjects = null,
        TimeSpan? ttl = null)
    {
        var accountKeyPair = KeyPair.FromSeed(
            string.IsNullOrWhiteSpace(accountSeed) ? TestAccountSeed : accountSeed);
        var claims = NatsJwt.NewUserClaims(userNkey);
        claims.Name = name;
        claims.Expires = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(60));
        claims.Audience = "$G";

        var pub = publishSubjects ?? [ControlSubject];
        var sub = subscribeSubjects ?? [LogSubject, "tenant.global.pong"];

        if (pub.Length > 0)
            claims.User.Pub.Allow = pub.ToList();
        if (sub.Length > 0)
            claims.User.Sub.Allow = sub.ToList();

        return NatsJwt.EncodeUserClaims(claims, accountKeyPair);
    }

    [Test]
    public void TryValidatePreIssuedNatsJwt_ShouldAcceptViewerJwtWithPubAllowAndExtractPermissions()
    {
        var service = CreateService();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();

        var jwt = IssueViewerJwt(userNkey);

        var accepted = service.TryValidatePreIssuedNatsJwt(
            jwt, out var expiresAtUtc, out var isSessionToken, out var subject,
            out var pubPerms, out var subPerms);

        Assert.That(accepted, Is.True,
            "JWT de usuário com nats.pub.allow NÃO deve ser rejeitado (causa do Authorization Violation).");
        Assert.That(expiresAtUtc, Is.GreaterThan(DateTime.UtcNow));
        Assert.That(isSessionToken, Is.False);
        Assert.That(subject, Is.EqualTo(userNkey));
        Assert.That(pubPerms, Does.Contain(ControlSubject));
        Assert.That(subPerms, Does.Contain(LogSubject));
    }

    [Test]
    public void TryValidatePreIssuedNatsJwt_ShouldFlagSessionTokensByName()
    {
        var service = CreateService();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();

        var jwt = IssueViewerJwt(userNkey, name: "session:9ea00794");

        var accepted = service.TryValidatePreIssuedNatsJwt(
            jwt, out _, out var isSessionToken, out var subject, out _, out _);

        Assert.That(accepted, Is.True);
        Assert.That(isSessionToken, Is.True);
        Assert.That(subject, Is.EqualTo(userNkey));
    }

    [Test]
    public void TryValidatePreIssuedNatsJwt_ShouldRejectExpiredJwt()
    {
        var service = CreateService();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();

        var jwt = IssueViewerJwt(userNkey, ttl: TimeSpan.FromMinutes(-5));

        var accepted = service.TryValidatePreIssuedNatsJwt(
            jwt, out _, out _, out _, out _, out _);

        Assert.That(accepted, Is.False);
    }

    [Test]
    public void TryValidatePreIssuedNatsJwt_ShouldRejectIssuerMismatch()
    {
        var service = CreateService();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();
        var otherAccountSeed = KeyPair.CreatePair(PrefixByte.Account).GetSeed();

        var jwt = IssueViewerJwt(userNkey, accountSeed: otherAccountSeed);

        var accepted = service.TryValidatePreIssuedNatsJwt(
            jwt, out _, out _, out _, out _, out _);

        Assert.That(accepted, Is.False,
            "JWT assinado por outra account não pode autorizar o viewer.");
    }

    [Test]
    public void TryValidatePreIssuedNatsJwt_ShouldRejectTamperedPayloadWithValidIssuer()
    {
        var service = CreateService();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();
        var jwt = IssueViewerJwt(userNkey, publishSubjects: ["tenant.a.only"]);
        var parts = jwt.Split('.');

        var payloadJson = Encoding.UTF8.GetString(DecodeBase64Url(parts[1]));
        using var doc = JsonDocument.Parse(payloadJson);
        var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(payloadJson)!;
        dict["exp"] = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds();
        var tamperedPayload = EncodeBase64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dict)));
        var tamperedJwt = $"{parts[0]}.{tamperedPayload}.{parts[2]}";

        var accepted = service.TryValidatePreIssuedNatsJwt(
            tamperedJwt, out _, out _, out _, out _, out _);

        Assert.That(accepted, Is.False,
            "Payload adulterado mesmo com iss igual ao da account deve falhar na verificação Ed25519.");
    }

    [Test]
    public void TryValidatePreIssuedNatsJwt_ShouldRejectForgedSignature()
    {
        var service = CreateService();
        using var accountKeyPair = KeyPair.FromSeed(TestAccountSeed);
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();

        var header = EncodeBase64Url(Encoding.UTF8.GetBytes("""{"typ":"JWT","alg":"ed25519-nkey"}"""));
        var payloadObj = new
        {
            jti = Guid.NewGuid().ToString("N"),
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            iss = accountKeyPair.GetPublicKey(),
            sub = userNkey,
            exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
            nats = new
            {
                pub = new { allow = new[] { ">" } },
                sub = new { allow = new[] { ">" } },
                type = "user",
                version = 2
            }
        };
        var payload = EncodeBase64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadObj)));
        var forgedSig = EncodeBase64Url(new byte[64]);
        var forgedJwt = $"{header}.{payload}.{forgedSig}";

        var accepted = service.TryValidatePreIssuedNatsJwt(
            forgedJwt, out _, out _, out _, out _, out _);

        Assert.That(accepted, Is.False);
    }

    [TestCase("none")]
    [TestCase("HS256")]
    [TestCase("ed25519")]
    public void TryValidatePreIssuedNatsJwt_ShouldRejectWrongAlgorithm(string alg)
    {
        var service = CreateService();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();
        var validJwt = IssueViewerJwt(userNkey);
        var parts = validJwt.Split('.');

        var badHeader = EncodeBase64Url(Encoding.UTF8.GetBytes($$"""{"typ":"JWT","alg":"{{alg}}"}"""));
        var modifiedJwt = $"{badHeader}.{parts[1]}.{parts[2]}";

        var accepted = service.TryValidatePreIssuedNatsJwt(
            modifiedJwt, out _, out _, out _, out _, out _);

        Assert.That(accepted, Is.False);
    }

    [Test]
    public void TryValidatePreIssuedNatsJwt_ShouldRejectFutureNotBefore()
    {
        var service = CreateService();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();
        using var accountKeyPair = KeyPair.FromSeed(TestAccountSeed);
        var claims = NatsJwt.NewUserClaims(userNkey);
        claims.NotBefore = DateTimeOffset.UtcNow.AddMinutes(5);
        claims.Expires = DateTimeOffset.UtcNow.AddMinutes(30);
        claims.Audience = "$G";
        var jwt = NatsJwt.EncodeUserClaims(claims, accountKeyPair);

        var accepted = service.TryValidatePreIssuedNatsJwt(
            jwt, out _, out _, out _, out _, out _);

        Assert.That(accepted, Is.False);
    }

    [TestCase("not-an-nkey")]
    [TestCase("ACCOUNT_KEY")]
    public void TryValidatePreIssuedNatsJwt_ShouldRejectInvalidSubjectNkeyPrefix(string mode)
    {
        var service = CreateService();
        using var accountKeyPair = KeyPair.FromSeed(TestAccountSeed);
        var subject = mode == "ACCOUNT_KEY"
            ? KeyPair.CreatePair(PrefixByte.Account).GetPublicKey()
            : "not-an-nkey";

        var claims = NatsJwt.NewUserClaims(subject);
        claims.Expires = DateTimeOffset.UtcNow.AddMinutes(30);
        claims.Audience = "$G";
        var jwt = NatsJwt.EncodeUserClaims(claims, accountKeyPair);

        var accepted = service.TryValidatePreIssuedNatsJwt(
            jwt, out _, out _, out _, out _, out _);

        Assert.That(accepted, Is.False);
    }

    [Test]
    public void TryValidatePreIssuedNatsJwt_ShouldEnforceExpectedUserNkey_WhenBound()
    {
        var service = CreateService();
        var userNkey1 = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();
        var userNkey2 = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();
        var jwt = IssueViewerJwt(userNkey1);

        Assert.That(service.TryValidatePreIssuedNatsJwt(
            jwt, expectedUserNkey: userNkey1, out _, out _, out _, out _, out _), Is.True);
        Assert.That(service.TryValidatePreIssuedNatsJwt(
            jwt, expectedUserNkey: userNkey2, out _, out _, out _, out _, out _), Is.False);
        Assert.That(service.TryValidatePreIssuedNatsJwt(
            jwt, expectedUserNkey: null, out _, out _, out _, out _, out _), Is.True);
    }

    [Test]
    public void ParseAuthRequest_ShouldAcceptValidServerSignedAuthRequest()
    {
        using var serverKeyPair = KeyPair.CreatePair(PrefixByte.Server);
        var serverPub = serverKeyPair.GetPublicKey();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();

        var claims = NatsJwt.NewAuthorizationRequestClaims(userNkey);
        claims.Issuer = serverPub;
        claims.Audience = "$G";
        claims.AuthorizationRequest.NatsServer = new NatsServerId { Id = serverPub, Name = "nats-1" };
        claims.AuthorizationRequest.UserNKey = userNkey;
        claims.AuthorizationRequest.NatsConnectOptions = new NatsConnectOptions { Token = "mdz_test" };
        var jwt = NatsJwt.EncodeAuthorizationRequestClaims(claims, serverKeyPair);

        var parsedNoPin = NatsAuthCalloutBackgroundService.ParseAuthRequest(jwt);
        var parsedPinned = NatsAuthCalloutBackgroundService.ParseAuthRequest(jwt, serverPub);

        Assert.That(parsedNoPin, Is.Not.Null);
        Assert.That(parsedNoPin!.Nats.Server!.Id, Is.EqualTo(serverPub));
        Assert.That(parsedNoPin.Nats.UserNkey, Is.EqualTo(userNkey));
        Assert.That(parsedPinned, Is.Not.Null);
    }

    [Test]
    public void ParseAuthRequest_ShouldAcceptRealServerShape_WhereSubIsAccountKey()
    {
        // REGRESSÃO (incidente 2026-11-18): o nats-server real (2.10+) monta o
        // auth request com sub = CHAVE PÚBLICA DA CONTA (não o user nkey —
        // este vai em nats.user_nkey). A checagem antiga (sub == user_nkey)
        // rejeitava TODOS os auth requests reais, derrubando console e agentes
        // (NATS auth_error / "Credencial NATS rejeitada"). Este teste reproduz
        // o shape capturado de um nats-server 2.10.7 real.
        using var serverKeyPair = KeyPair.CreatePair(PrefixByte.Server);
        var serverPub = serverKeyPair.GetPublicKey();
        var accountPub = KeyPair.CreatePair(PrefixByte.Account).GetPublicKey();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();

        var claims = NatsJwt.NewAuthorizationRequestClaims(userNkey);
        claims.Subject = accountPub; // ← o nats-server real define sub = account
        claims.Issuer = serverPub;
        claims.Audience = "nats-authorization-request";
        claims.AuthorizationRequest.NatsServer = new NatsServerId
        {
            Id = serverPub,
            Name = "discovery-nats",
            Host = "0.0.0.0",
            Version = "2.10.7",
        };
        claims.AuthorizationRequest.UserNKey = userNkey;
        claims.AuthorizationRequest.NatsConnectOptions = new NatsConnectOptions
        {
            Token = "mdz_probe_token",
            Name = "probe",
            Lang = "py",
            Protocol = 1,
        };
        var jwt = NatsJwt.EncodeAuthorizationRequestClaims(claims, serverKeyPair);

        var parsed = NatsAuthCalloutBackgroundService.ParseAuthRequest(jwt, null, out var failureReason);

        Assert.That(parsed, Is.Not.Null, $"falha: {failureReason}");
        Assert.That(parsed!.Nats.UserNkey, Is.EqualTo(userNkey));
        Assert.That(failureReason, Is.Null);
    }

    [Test]
    public void ParseAuthRequest_ShouldRejectInvalidOrTamperedServerSignature()
    {
        using var serverKeyPair = KeyPair.CreatePair(PrefixByte.Server);
        var serverPub = serverKeyPair.GetPublicKey();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();

        var claims = NatsJwt.NewAuthorizationRequestClaims(userNkey);
        claims.Issuer = serverPub;
        claims.AuthorizationRequest.NatsServer = new NatsServerId { Id = serverPub, Name = "nats-1" };
        claims.AuthorizationRequest.UserNKey = userNkey;
        var jwt = NatsJwt.EncodeAuthorizationRequestClaims(claims, serverKeyPair);
        var parts = jwt.Split('.');

        var payloadJson = Encoding.UTF8.GetString(DecodeBase64Url(parts[1]));
        var tamperedJson = payloadJson.Replace(userNkey, KeyPair.CreatePair(PrefixByte.User).GetPublicKey(), StringComparison.Ordinal);
        var tamperedJwt = $"{parts[0]}.{EncodeBase64Url(Encoding.UTF8.GetBytes(tamperedJson))}.{parts[2]}";

        Assert.That(NatsAuthCalloutBackgroundService.ParseAuthRequest(tamperedJwt), Is.Null);

        var forgedSigJwt = $"{parts[0]}.{parts[1]}.{EncodeBase64Url(new byte[64])}";
        Assert.That(NatsAuthCalloutBackgroundService.ParseAuthRequest(forgedSigJwt), Is.Null);
    }

    [Test]
    public void ParseAuthRequest_ShouldRejectIssuerMismatchAndNonServerNkeyPrefix()
    {
        using var serverKeyPair1 = KeyPair.CreatePair(PrefixByte.Server);
        using var serverKeyPair2 = KeyPair.CreatePair(PrefixByte.Server);
        using var accountKeyPair = KeyPair.CreatePair(PrefixByte.Account);
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();

        // iss != server_id.id
        var mismatchClaims = NatsJwt.NewAuthorizationRequestClaims(userNkey);
        mismatchClaims.Issuer = serverKeyPair1.GetPublicKey();
        mismatchClaims.AuthorizationRequest.NatsServer = new NatsServerId { Id = serverKeyPair2.GetPublicKey() };
        mismatchClaims.AuthorizationRequest.UserNKey = userNkey;
        var mismatchJwt = NatsJwt.EncodeAuthorizationRequestClaims(mismatchClaims, serverKeyPair1);
        Assert.That(NatsAuthCalloutBackgroundService.ParseAuthRequest(mismatchJwt), Is.Null);

        // Non-server prefix ('A...' instead of 'N...') — craft signed JWT directly since NatsJwt.EncodeAuthorizationRequestClaims enforces 'N' prefix at encode time
        var wrongHeader = EncodeBase64Url(Encoding.UTF8.GetBytes("""{"typ":"JWT","alg":"ed25519-nkey"}"""));
        var accountPub = accountKeyPair.GetPublicKey();
        var wrongPayloadObj = new
        {
            jti = Guid.NewGuid().ToString("N"),
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            iss = accountPub,
            sub = userNkey,
            nats = new
            {
                user_nkey = userNkey,
                server_id = new { id = accountPub, name = "forged" },
                connect_opts = new { auth_token = "mdz_test" }
            }
        };
        var wrongPayload = EncodeBase64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(wrongPayloadObj)));
        var signingInput = Encoding.ASCII.GetBytes($"{wrongHeader}.{wrongPayload}");
        var sigBytes = new byte[64];
        accountKeyPair.Sign(signingInput, sigBytes);
        var wrongPrefixJwt = $"{wrongHeader}.{wrongPayload}.{EncodeBase64Url(sigBytes)}";
        Assert.That(NatsAuthCalloutBackgroundService.ParseAuthRequest(wrongPrefixJwt), Is.Null);
    }

    [Test]
    public void ParseAuthRequest_ShouldRejectUntrustedServerPublicKey_WhenPinned()
    {
        using var serverKeyPair1 = KeyPair.CreatePair(PrefixByte.Server);
        using var serverKeyPair2 = KeyPair.CreatePair(PrefixByte.Server);
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();

        var claims = NatsJwt.NewAuthorizationRequestClaims(userNkey);
        claims.Issuer = serverKeyPair1.GetPublicKey();
        claims.AuthorizationRequest.NatsServer = new NatsServerId { Id = serverKeyPair1.GetPublicKey() };
        claims.AuthorizationRequest.UserNKey = userNkey;
        var jwt = NatsJwt.EncodeAuthorizationRequestClaims(claims, serverKeyPair1);

        Assert.That(
            NatsAuthCalloutBackgroundService.ParseAuthRequest(jwt, serverKeyPair2.GetPublicKey()),
            Is.Null);
    }

    [Test]
    public void TryDecryptRequestPayload_ShouldEnforceXKey_WhenConfigured()
    {
        using var accountXKey = KeyPair.CreatePair(PrefixByte.Curve);
        using var serverXKey = KeyPair.CreatePair(PrefixByte.Curve);
        using var wrongServerXKey = KeyPair.CreatePair(PrefixByte.Curve);
        var xKeySeed = accountXKey.GetSeed();
        const string sampleJwt = "header.payload.sig";
        var plaintextBytes = Encoding.UTF8.GetBytes(sampleJwt);

        // Missing header
        Assert.That(
            NatsAuthCalloutBackgroundService.TryDecryptRequestPayload(plaintextBytes, xKeySeed, null, out _, out var errMissing),
            Is.False);
        Assert.That(errMissing, Does.Contain("missing xkey"));

        // Empty header
        Assert.That(
            NatsAuthCalloutBackgroundService.TryDecryptRequestPayload(plaintextBytes, xKeySeed, "  ", out _, out var errEmpty),
            Is.False);
        Assert.That(errEmpty, Does.Contain("empty xkey"));

        // Plaintext payload rejected when xkey configured
        Assert.That(
            NatsAuthCalloutBackgroundService.TryDecryptRequestPayload(plaintextBytes, xKeySeed, serverXKey.GetPublicKey(), out _, out var errPlain),
            Is.False);
        Assert.That(errPlain, Does.Contain("decryption failed"));

        // Sealed by serverXKey -> opened with accountXKey + wrong sender key fails
        var sealedBytes = serverXKey.Seal(plaintextBytes, accountXKey.GetPublicKey());
        Assert.That(
            NatsAuthCalloutBackgroundService.TryDecryptRequestPayload(sealedBytes, xKeySeed, wrongServerXKey.GetPublicKey(), out _, out _),
            Is.False);

        // Valid sealed payload succeeds
        Assert.That(
            NatsAuthCalloutBackgroundService.TryDecryptRequestPayload(sealedBytes, xKeySeed, serverXKey.GetPublicKey(), out var decrypted, out var errOk),
            Is.True);
        Assert.That(decrypted, Is.EqualTo(sampleJwt));
        Assert.That(errOk, Is.Null);
    }

    [TestCase("pad")]
    [TestCase("plus")]
    [TestCase("slash")]
    [TestCase("space")]
    public void TryValidatePreIssuedNatsJwt_ShouldRejectNonBase64UrlEncodingInSignature(string variant)
    {
        var service = CreateService();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();
        var jwt = IssueViewerJwt(userNkey);
        var parts = jwt.Split('.');

        var corruptedSig = variant switch
        {
            "pad" => parts[2] + "==",
            "plus" => parts[2].Replace('-', '+') + (parts[2].Contains('-') ? "" : "+"),
            "slash" => parts[2].Replace('_', '/') + (parts[2].Contains('_') ? "" : "/"),
            "space" => " " + parts[2],
            _ => parts[2]
        };

        // Ensure corruptedSig actually differs or triggers non-Base64URL rejection
        var badJwt = $"{parts[0]}.{parts[1]}.{corruptedSig}";
        var accepted = service.TryValidatePreIssuedNatsJwt(
            badJwt, out _, out _, out _, out _, out _);

        Assert.That(accepted, Is.False);
    }

    [Test]
    public void TryValidatePreIssuedNatsJwt_ShouldRejectNonStringTypeAndNonIntVersion()
    {
        var service = CreateService();
        using var accountKeyPair = KeyPair.FromSeed(TestAccountSeed);
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();
        var header = EncodeBase64Url(Encoding.UTF8.GetBytes("""{"typ":"JWT","alg":"ed25519-nkey"}"""));

        string SignPayload(object payloadObj)
        {
            var payload = EncodeBase64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadObj)));
            var sig = new byte[64];
            accountKeyPair.Sign(Encoding.ASCII.GetBytes($"{header}.{payload}"), sig);
            return $"{header}.{payload}.{EncodeBase64Url(sig)}";
        }

        var nonStringTypeJwt = SignPayload(new
        {
            jti = Guid.NewGuid().ToString("N"),
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            iss = accountKeyPair.GetPublicKey(),
            sub = userNkey,
            exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
            nats = new { type = 123, version = 2 }
        });

        var stringVersionJwt = SignPayload(new
        {
            jti = Guid.NewGuid().ToString("N"),
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            iss = accountKeyPair.GetPublicKey(),
            sub = userNkey,
            exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
            nats = new { type = "user", version = "1" }
        });

        Assert.That(service.TryValidatePreIssuedNatsJwt(nonStringTypeJwt, out _, out _, out _, out _, out _), Is.False);
        Assert.That(service.TryValidatePreIssuedNatsJwt(stringVersionJwt, out _, out _, out _, out _, out _), Is.False);
    }

    [Test]
    public void ParseAuthRequest_ShouldFailClosed_WhenServerPublicKeyHasOnlyCommas()
    {
        using var serverKeyPair = KeyPair.CreatePair(PrefixByte.Server);
        var serverPub = serverKeyPair.GetPublicKey();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();

        var claims = NatsJwt.NewAuthorizationRequestClaims(userNkey);
        claims.Issuer = serverPub;
        claims.AuthorizationRequest.NatsServer = new NatsServerId { Id = serverPub, Name = "nats-1" };
        claims.AuthorizationRequest.UserNKey = userNkey;
        var jwt = NatsJwt.EncodeAuthorizationRequestClaims(claims, serverKeyPair);

        Assert.That(NatsAuthCalloutBackgroundService.ParseAuthRequest(jwt, ",,,"), Is.Null);
    }

    private static string EncodeBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string input)
    {
        var base64 = input.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }

        return Convert.FromBase64String(base64);
    }
}
