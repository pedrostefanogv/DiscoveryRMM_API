using Discovery.Api.Services;
using Discovery.Core.Enums;

namespace Discovery.Tests;

/// <summary>
/// Testes do canal NATS do editor de Registro do Windows.
///
/// O ponto central: os subjects <c>.registry.*</c> são concedidos SOMENTE para
/// sessões de kind <see cref="RemoteSessionKind.Registry"/> — uma sessão de tela,
/// terminal ou arquivos NÃO recebe acesso ao registro do endpoint. Isto é mais
/// restritivo que <c>.proc.*</c> (concedido a qualquer sessão) de propósito: o
/// registro contém configuração sensível do sistema operacional.
///
/// A imposição é do BROKER: se o subject não está na credencial, um viewer
/// adulterado recebe -ERR Permissions Violation ao tentar publicar. É por isso
/// que estes testes asseguram a lista de subjects, e não o comportamento da UI.
/// </summary>
public sealed class RegistrySessionPermissionsTests
{
    private const string Subject = "tenant.a.site.b.agent.c.remote.session.d";

    [Test]
    public void Build_Registry_ConcedeSubjectsDeRegistro()
    {
        var (pub, sub) = RemoteSessionViewerPermissions.Build(
            Subject, RemoteSessionRights.None, RemoteSessionKind.Registry);

        Assert.That(pub, Does.Contain(Subject + ".registry.req"), "viewer precisa publicar pedidos");
        Assert.That(sub, Does.Contain(Subject + ".registry.resp"), "viewer precisa receber respostas");
        Assert.That(sub, Does.Contain(Subject + ".registry.ready"), "viewer precisa saber que o subscribe está ativo");
    }

    [Test]
    public void Build_Registry_NaoConcedeSubjectsDeTunel()
    {
        var (pub, sub) = RemoteSessionViewerPermissions.Build(
            Subject, RemoteSessionRights.None, RemoteSessionKind.Registry);

        // Túnel (proxy/RDP) é recortado por KIND no helper — é a parte do ACL que
        // realmente varia por tipo de sessão. Uma sessão de registro não deve
        // abrir caminho para a rede interna da máquina nem para a porta 3389.
        Assert.That(pub, Does.Not.Contain(Subject + ".proxy.req"));
        Assert.That(pub, Does.Not.Contain(Subject + ".tunnel.in"));
        Assert.That(sub, Does.Not.Contain(Subject + ".proxy.resp"));
        Assert.That(sub, Does.Not.Contain(Subject + ".tunnel.out"));

        // NOTA DELIBERADA sobre o que o helper NÃO faz: os subjects de terminal,
        // arquivos e processos são concedidos a QUALQUER sessão (comportamento
        // anterior, preservado aqui de propósito — mudá-lo seria uma alteração de
        // contrato fora do escopo desta feature). O console escolhe qual módulo
        // renderizar pelo kind; um viewer malicioso com uma sessão de registro
        // poderia, em tese, falar com esses canais. Está registrado como
        // observação em vez de ser silenciado por um assert que não reflete o
        // código real.
        Assert.That(pub, Does.Contain(Subject + ".term.in"));
        Assert.That(pub, Does.Contain(Subject + ".files.req"));
    }

    [Test]
    public void Build_OutrosKinds_NaoRecebemSubjectsDeRegistro()
    {
        // ESTE é o teste que impede uma escalada silenciosa de privilégio: se
        // alguém no futuro tornar .registry.* incondicional (como .proc.* é
        // hoje), uma sessão de tela passaria a ler o registro do endpoint sem
        // que ninguém tivesse decidido isso.
        var kinds = new[]
        {
            RemoteSessionKind.Screen,
            RemoteSessionKind.Terminal,
            RemoteSessionKind.Files,
            RemoteSessionKind.Proxy,
            RemoteSessionKind.Rdp,
            RemoteSessionKind.Processes,
            RemoteSessionKind.All,
        };

        foreach (var kind in kinds)
        {
            var (pub, sub) = RemoteSessionViewerPermissions.Build(
                Subject, RemoteSessionRights.None, kind);

            Assert.That(pub, Does.Not.Contain(Subject + ".registry.req"),
                $"kind {kind} não deveria poder publicar em registry.req");
            Assert.That(sub, Does.Not.Contain(Subject + ".registry.resp"),
                $"kind {kind} não deveria receber registry.resp");
            Assert.That(sub, Does.Not.Contain(Subject + ".registry.ready"),
                $"kind {kind} não deveria receber registry.ready");
        }
    }

    [Test]
    public void Build_KindLegadoNull_NaoRecebeRegistro()
    {
        // O overload sem kind existe para chamadores legados (proxy concedido).
        // Para o registro o default é NEGAR: conceder ampliaria o que uma sessão
        // já existente pode fazer, sem que ninguém tenha pedido.
        var (pub, sub) = RemoteSessionViewerPermissions.Build(Subject);

        Assert.That(pub, Does.Not.Contain(Subject + ".registry.req"));
        Assert.That(sub, Does.Not.Contain(Subject + ".registry.resp"));
        Assert.That(sub, Does.Not.Contain(Subject + ".registry.ready"));
    }

    [Test]
    public void Build_NoRegistry_RevogaSubjectsDeRegistro()
    {
        var (pub, sub) = RemoteSessionViewerPermissions.Build(
            Subject, RemoteSessionRights.NoRegistry, RemoteSessionKind.Registry);

        Assert.That(pub, Does.Not.Contain(Subject + ".registry.req"));
        Assert.That(sub, Does.Not.Contain(Subject + ".registry.resp"));
        Assert.That(sub, Does.Not.Contain(Subject + ".registry.ready"));
    }

    [Test]
    public void Build_NoRegistry_NaoAfetaOutrasCapacidades()
    {
        // NoRegistry é cirúrgico: revogar o registro não pode derrubar o
        // terminal nem os arquivos, que são capacidades independentes.
        var (pub, sub) = RemoteSessionViewerPermissions.Build(
            Subject, RemoteSessionRights.NoRegistry, RemoteSessionKind.Registry);

        Assert.That(pub, Does.Contain(Subject + ".term.in"));
        Assert.That(pub, Does.Contain(Subject + ".files.req"));
        Assert.That(sub, Does.Contain(Subject + ".term.out"));
        Assert.That(sub, Does.Contain(Subject + ".files.data"));
    }

    [Test]
    public void Build_ViewOnly_NaoAfetaRegistro()
    {
        // ViewOnly restringe input de teclado/mouse — não tem relação com o
        // canal do registro, que é textual e sempre exige confirmação na UI
        // para mutação. Verificar isto evita que alguém "reuse" ViewOnly como
        // se fosse um modo somente-leitura de registro (não é).
        var (pub, _) = RemoteSessionViewerPermissions.Build(
            Subject, RemoteSessionRights.ViewOnly, RemoteSessionKind.Registry);

        Assert.That(pub, Does.Not.Contain(Subject + ".input"));
        Assert.That(pub, Does.Contain(Subject + ".registry.req"));
    }
}
