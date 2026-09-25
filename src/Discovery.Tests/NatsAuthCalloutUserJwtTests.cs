using Discovery.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Jwt;
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
}
