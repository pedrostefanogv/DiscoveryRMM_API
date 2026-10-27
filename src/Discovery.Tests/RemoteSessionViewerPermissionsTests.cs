using Discovery.Api.Services;

namespace Discovery.Tests;

/// <summary>
/// Testes unitários do helper de permissões/TTL do viewer de sessão remota
/// (2026-10-27): fonte única usada pela resposta de start e por /nats-credentials.
/// </summary>
public sealed class RemoteSessionViewerPermissionsTests
{
    [Test]
    public void Build_ContemSubjectsDeTerminal()
    {
        var subject = "tenant.a.site.b.agent.c.remote.session.d";
        var (pub, sub) = RemoteSessionViewerPermissions.Build(subject);

        Assert.That(pub, Does.Contain(subject + ".term.in"));
        Assert.That(pub, Does.Contain(subject + ".completion.req"));
        Assert.That(pub, Does.Contain(subject + ".control"));
        Assert.That(sub, Does.Contain(subject + ".term.out"));
        Assert.That(sub, Does.Contain(subject + ".term.ready"));
        Assert.That(sub, Does.Contain(subject + ".stats"));
        Assert.That(sub, Does.Contain(subject + ".completion.res"));
        Assert.That(sub, Does.Contain(subject + ".control"));
    }

    [Test]
    public void Build_NaoVazaSubjectDeOutraSessao()
    {
        var (pub, sub) = RemoteSessionViewerPermissions.Build("tenant.a.site.b.agent.c.remote.session.d");

        Assert.That(pub.All(s => s.Contains("remote.session.d.")), Is.True, "pub com subject de outra sessao");
        Assert.That(sub.All(s => s.Contains("remote.session.d.")), Is.True, "sub com subject de outra sessao");
    }

    [Test]
    public void Ttl_CobreSessaoComMargem()
    {
        var expires = DateTime.UtcNow.AddMinutes(30);
        var ttl = RemoteSessionViewerPermissions.TtlMinutes(expires);

        // 30 min restantes + margem de 10 => 40.
        Assert.That(ttl, Is.EqualTo(40));
    }

    [Test]
    public void Ttl_Expirada_Fallback30()
    {
        var ttl = RemoteSessionViewerPermissions.TtlMinutes(DateTime.UtcNow.AddMinutes(-5));
        Assert.That(ttl, Is.EqualTo(30));
    }
}
