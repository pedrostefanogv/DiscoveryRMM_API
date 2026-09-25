using System.Text.Json;
using Discovery.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Jwt;
using NATS.NKeys;

namespace Discovery.Tests;

/// <summary>
/// Regressão do "Authorization Violation" no CONNECT do console de remote debug.
///
/// O auth callout valida o JWT NATS pré-emitido para o viewer. A lib
/// NATS.Jwt 1.0.1 lança NatsJwtException ao decodificar um JWT de usuário
/// gerado por NatsJwt.EncodeUserClaims quando o claim "nats" contém
/// "pub.allow" — exatamente o caso do console, que precisa PUBLICAR o ping no
/// canal de controle. Com DecodeUserClaims, TODO JWT do viewer era rejeitado
/// ("Invalid user token.") e o NATS respondia Authorization Violation.
/// </summary>
[TestFixture]
public class NatsAuthCalloutUserJwtTests
{
    private static readonly string TestAccountSeed =
        KeyPair.CreatePair(PrefixByte.Account).GetSeed();

    private NatsAuthCalloutBackgroundService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nats:AccountSeed"] = TestAccountSeed,
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
    private static string IssueViewerJwt(string userNkey, string[] subscribeSubjects, string[] publishSubjects)
    {
        var accountKeyPair = KeyPair.FromSeed(TestAccountSeed);
        var claims = NatsJwt.NewUserClaims(userNkey);
        claims.Name = "user:regression";
        claims.Expires = DateTimeOffset.UtcNow.AddMinutes(60);
        claims.Audience = "$G";

        if (publishSubjects.Length > 0)
            claims.User.Pub.Allow = publishSubjects.ToList();
        if (subscribeSubjects.Length > 0)
            claims.User.Sub.Allow = subscribeSubjects.ToList();

        return NatsJwt.EncodeUserClaims(claims, accountKeyPair);
    }

    [Test]
    public void TryValidatePreIssuedNatsUserJwt_ShouldAcceptJwtWithPubAllow()
    {
        var service = CreateService();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();

        var jwt = IssueViewerJwt(
            userNkey,
            subscribeSubjects: ["tenant.*.site.*.agent.*.remote-debug.log", "tenant.global.pong"],
            publishSubjects: ["tenant.c.site.s.agent.a.remote-debug.control"]);

        var accepted = service.TryValidatePreIssuedNatsUserJwt(jwt, userNkey, out var expiresAtUtc);

        Assert.That(accepted, Is.True,
            "JWT de usuário com nats.pub.allow NÃO deve ser rejeitado (causa do Authorization Violation).");
        Assert.That(expiresAtUtc, Is.GreaterThan(DateTime.UtcNow));
    }

    [Test]
    public void TryValidatePreIssuedNatsUserJwt_ShouldRejectSubjectMismatch()
    {
        var service = CreateService();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();
        var otherNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();

        var jwt = IssueViewerJwt(
            userNkey,
            subscribeSubjects: ["tenant.*.site.*.agent.*.remote-debug.log"],
            publishSubjects: ["tenant.c.site.s.agent.a.remote-debug.control"]);

        var accepted = service.TryValidatePreIssuedNatsUserJwt(jwt, otherNkey, out _);

        Assert.That(accepted, Is.False, "userNkey divergente deve ser rejeitado.");
    }

    [Test]
    public void TryValidatePreIssuedNatsUserJwt_ShouldRejectExpiredJwt()
    {
        var service = CreateService();
        var userNkey = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();
        var accountKeyPair = KeyPair.FromSeed(TestAccountSeed);

        var claims = NatsJwt.NewUserClaims(userNkey);
        claims.Expires = DateTimeOffset.UtcNow.AddMinutes(-5);
        claims.User.Sub.Allow = ["tenant.*.site.*.agent.*.remote-debug.log"];
        claims.User.Pub.Allow = ["tenant.c.site.s.agent.a.remote-debug.control"];
        var jwt = NatsJwt.EncodeUserClaims(claims, accountKeyPair);

        var accepted = service.TryValidatePreIssuedNatsUserJwt(jwt, userNkey, out _);

        Assert.That(accepted, Is.False);
    }
}
