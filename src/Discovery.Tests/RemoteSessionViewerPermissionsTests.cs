using Discovery.Api.Services;
using Discovery.Core.Enums;

namespace Discovery.Tests;

/// <summary>
/// Testes unitários do helper de permissões/TTL do viewer de sessão remota
/// (2026-10-27): fonte única usada pela resposta de start e por /nats-credentials.
/// </summary>
public sealed class RemoteSessionViewerPermissionsTests
{
    private const string Subject = "tenant.a.site.b.agent.c.remote.session.d";

    [Test]
    public void Build_ContemSubjectsDeTerminal()
    {
        var (pub, sub) = RemoteSessionViewerPermissions.Build(Subject);

        Assert.That(pub, Does.Contain(Subject + ".term.in"));
        Assert.That(pub, Does.Contain(Subject + ".completion.req"));
        Assert.That(pub, Does.Contain(Subject + ".control"));
        Assert.That(sub, Does.Contain(Subject + ".term.out"));
        Assert.That(sub, Does.Contain(Subject + ".term.ready"));
        Assert.That(sub, Does.Contain(Subject + ".stats"));
        Assert.That(sub, Does.Contain(Subject + ".completion.res"));
        Assert.That(sub, Does.Contain(Subject + ".control"));
    }

    [Test]
    public void Build_FilesEAll_ConcedeFilesData_ENoFilesBloqueia()
    {
        var (pubFiles, subFiles) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.None, RemoteSessionKind.Files);
        Assert.That(pubFiles, Does.Contain(Subject + ".files.req"));
        Assert.That(subFiles, Does.Contain(Subject + ".files.ready"));
        Assert.That(subFiles, Does.Contain(Subject + ".files.resp"));
        Assert.That(subFiles, Does.Contain(Subject + ".files.data"));
        Assert.That(subFiles, Does.Contain(Subject + ".files.progress"));

        var (pubAll, subAll) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.None, RemoteSessionKind.All);
        Assert.That(subAll, Does.Contain(Subject + ".files.data"));
        Assert.That(pubAll, Does.Contain(Subject + ".files.req"));

        var (pubNoFiles, subNoFiles) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.NoFiles, RemoteSessionKind.Files);
        Assert.That(pubNoFiles, Does.Not.Contain(Subject + ".files.req"));
        Assert.That(subNoFiles, Does.Not.Contain(Subject + ".files.data"));
        Assert.That(subNoFiles, Does.Not.Contain(Subject + ".files.resp"));
        Assert.That(subNoFiles, Does.Not.Contain(Subject + ".files.ready"));
    }

    [Test]
    public void Build_Proxy_ConcedeSubjectsDeProxyETunnel()
    {
        var (pub, sub) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.None, RemoteSessionKind.Proxy);

        Assert.That(pub, Does.Contain(Subject + ".proxy.req"));
        Assert.That(pub, Does.Contain(Subject + ".tunnel.in"));
        Assert.That(sub, Does.Contain(Subject + ".proxy.resp"));
        Assert.That(sub, Does.Contain(Subject + ".proxy.ready"));
        Assert.That(sub, Does.Contain(Subject + ".tunnel.out"));
    }

    [Test]
    public void Build_Rdp_ConcedeProxyReadyETunnelSemProxyHttp()
    {
        var (pub, sub) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.None, RemoteSessionKind.Rdp);

        Assert.That(pub, Does.Contain(Subject + ".tunnel.in"));
        Assert.That(sub, Does.Contain(Subject + ".proxy.ready"));
        Assert.That(sub, Does.Contain(Subject + ".tunnel.out"));
        Assert.That(pub, Does.Not.Contain(Subject + ".proxy.req"));
        Assert.That(sub, Does.Not.Contain(Subject + ".proxy.resp"));
    }

    [Test]
    public void Build_All_RespeitaNoProxyENoRdpIndependentemente()
    {
        // NoProxy bloqueia HTTP proxy (.proxy.req/.proxy.resp) mas mantém túnel para RDP.
        var (pubNoProxy, subNoProxy) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.NoProxy, RemoteSessionKind.All);
        Assert.That(pubNoProxy, Does.Not.Contain(Subject + ".proxy.req"));
        Assert.That(subNoProxy, Does.Not.Contain(Subject + ".proxy.resp"));
        Assert.That(pubNoProxy, Does.Contain(Subject + ".tunnel.in"));
        Assert.That(subNoProxy, Does.Contain(Subject + ".tunnel.out"));
        Assert.That(subNoProxy, Does.Contain(Subject + ".proxy.ready"));

        // NoRdp em kind=All mantém túnel para Proxy quando !NoProxy.
        var (pubNoRdp, subNoRdp) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.NoRdp, RemoteSessionKind.All);
        Assert.That(pubNoRdp, Does.Contain(Subject + ".proxy.req"));
        Assert.That(subNoRdp, Does.Contain(Subject + ".proxy.resp"));
        Assert.That(pubNoRdp, Does.Contain(Subject + ".tunnel.in"));
        Assert.That(subNoRdp, Does.Contain(Subject + ".tunnel.out"));
        Assert.That(subNoRdp, Does.Contain(Subject + ".proxy.ready"));

        // NoProxy | NoRdp remove tudo de proxy e túnel.
        var (pubBothBlocked, subBothBlocked) = RemoteSessionViewerPermissions.Build(
            Subject, RemoteSessionRights.NoProxy | RemoteSessionRights.NoRdp, RemoteSessionKind.All);
        Assert.That(pubBothBlocked, Does.Not.Contain(Subject + ".proxy.req"));
        Assert.That(pubBothBlocked, Does.Not.Contain(Subject + ".tunnel.in"));
        Assert.That(subBothBlocked, Does.Not.Contain(Subject + ".proxy.resp"));
        Assert.That(subBothBlocked, Does.Not.Contain(Subject + ".proxy.ready"));
        Assert.That(subBothBlocked, Does.Not.Contain(Subject + ".tunnel.out"));

        // NoRdp em kind=Rdp bloqueia todo o túnel.
        var (pubRdpBlocked, subRdpBlocked) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.NoRdp, RemoteSessionKind.Rdp);
        Assert.That(pubRdpBlocked, Does.Not.Contain(Subject + ".tunnel.in"));
        Assert.That(subRdpBlocked, Does.Not.Contain(Subject + ".tunnel.out"));
        Assert.That(subRdpBlocked, Does.Not.Contain(Subject + ".proxy.ready"));

        // Screen puro não recebe proxy nem tunnel.
        var (pubScreen, subScreen) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.None, RemoteSessionKind.Screen);
        Assert.That(pubScreen, Does.Not.Contain(Subject + ".proxy.req"));
        Assert.That(pubScreen, Does.Not.Contain(Subject + ".tunnel.in"));
        Assert.That(subScreen, Does.Not.Contain(Subject + ".proxy.ready"));
        Assert.That(subScreen, Does.Not.Contain(Subject + ".tunnel.out"));
    }

    [Test]
    public void Build_NaoVazaSubjectDeOutraSessao()
    {
        var (pub, sub) = RemoteSessionViewerPermissions.Build(Subject);

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
    public void Build_ConcedeSubjectsWebRtcApenasParaKindsElegiveis()
    {
        // Terminal e processes entraram junto com o caminho de dados deles no agente
        // e no console: negociar sem caminho de dados faria o selo mentir.
        foreach (var kind in new[]
                     {
                     RemoteSessionKind.Screen,
                     RemoteSessionKind.Rdp,
                     RemoteSessionKind.Files,
                     RemoteSessionKind.Proxy,
                     RemoteSessionKind.All,
                     RemoteSessionKind.Terminal,
                     RemoteSessionKind.Processes
                     })
        {
            var (pub, sub) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.None, kind);
            Assert.That(pub, Does.Contain(Subject + ".webrtc.in"), $"pub faltando .webrtc.in para {kind}");
            Assert.That(sub, Does.Contain(Subject + ".webrtc.out"), $"sub faltando .webrtc.out para {kind}");
        }

        // Registry SEGUE excluído: ali o gargalo é o TAMANHO da página (500 itens),
        // não o transporte.
        foreach (var kind in new[] { RemoteSessionKind.Registry })
        {
            var (pub, sub) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.None, kind);
            Assert.That(pub, Does.Not.Contain(Subject + ".webrtc.in"), $"pub não deveria ter .webrtc.in para {kind}");
            Assert.That(sub, Does.Not.Contain(Subject + ".webrtc.out"), $"sub não deveria ter .webrtc.out para {kind}");
        }
    }

    [Test]
    public void Ttl_Expirada_Fallback30()
    {
        var ttl = RemoteSessionViewerPermissions.TtlMinutes(DateTime.UtcNow.AddMinutes(-5));
        Assert.That(ttl, Is.EqualTo(30));
    }

    [Test]
    public void Build_ConcedeAnnotateApenasParaTelaSemNoDesktop()
    {
        var (pubScreen, _) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.None, RemoteSessionKind.Screen);
        var (pubAll, _) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.None, RemoteSessionKind.All);
        var (pubLegacy, _) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.None, null);
        var (pubViewOnly, _) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.ViewOnly, RemoteSessionKind.Screen);

        Assert.That(pubScreen, Does.Contain(Subject + ".annotate"));
        Assert.That(pubAll, Does.Contain(Subject + ".annotate"));
        Assert.That(pubLegacy, Does.Contain(Subject + ".annotate"));
        Assert.That(pubViewOnly, Does.Contain(Subject + ".annotate"));

        var (pubNoDesktop, _) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.NoDesktop, RemoteSessionKind.Screen);
        Assert.That(pubNoDesktop, Does.Not.Contain(Subject + ".annotate"));

        foreach (var kind in new[]
        {
            RemoteSessionKind.Terminal,
            RemoteSessionKind.Files,
            RemoteSessionKind.Proxy,
            RemoteSessionKind.Rdp,
            RemoteSessionKind.Registry,
            RemoteSessionKind.Processes
        })
        {
            var (pub, _) = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.None, kind);
            Assert.That(pub, Does.Not.Contain(Subject + ".annotate"), $"pub não deveria ter .annotate para {kind}");
        }
    }
}
