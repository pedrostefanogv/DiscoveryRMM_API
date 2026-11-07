using Discovery.Api.Services;
using Discovery.Core.Enums;

namespace Discovery.Tests;

/// <summary>
/// Restrições granulares de sessão remota (Fase B4 da auditoria de identidade).
///
/// O valor destas flags está em serem impostas no SERVIDOR (como ACL do NATS), não
/// como dica de UI: a credencial do viewer deixa de receber os subjects das
/// capacidades restringidas, então um cliente web adulterado que tente publicar em
/// <c>...input</c> recebe -ERR Permissions Violation do próprio broker.
///
/// Estes testes são o contrato dessa imposição. Uma regressão aqui significa
/// conceder controle total a quem deveria apenas visualizar — o pedido mais comum
/// de clientes com exigência de LGPD.
/// </summary>
[TestFixture]
public class RemoteSessionRightsTests
{
    private const string Subject = "tenant.abc.site.def.agent.123.remote.session.456";

    private static (string[] Publish, string[] Subscribe) Build(RemoteSessionRights rights)
        => RemoteSessionViewerPermissions.Build(Subject, rights);

    [Test]
    public void Default_none_keeps_full_capability()
    {
        var (pub, sub) = Build(RemoteSessionRights.None);

        Assert.That(pub, Does.Contain($"{Subject}.input"));
        Assert.That(pub, Does.Contain($"{Subject}.term.in"));
        Assert.That(pub, Does.Contain($"{Subject}.files.req"));
        Assert.That(sub, Does.Contain($"{Subject}.term.out"));
        Assert.That(sub, Does.Contain($"{Subject}.clipboard"));
    }

    [Test]
    public void Backwards_compatible_overload_matches_none()
    {
        var legacy = RemoteSessionViewerPermissions.Build(Subject);
        var explicitNone = RemoteSessionViewerPermissions.Build(Subject, RemoteSessionRights.None);

        Assert.That(legacy.Publish, Is.EquivalentTo(explicitNone.Publish));
        Assert.That(legacy.Subscribe, Is.EquivalentTo(explicitNone.Subscribe));
    }

    [Test]
    public void ViewOnly_removes_input_and_clipboard_but_keeps_frames()
    {
        var (pub, sub) = Build(RemoteSessionRights.ViewOnly);

        Assert.That(pub, Does.Not.Contain($"{Subject}.input"), "view-only não pode enviar input");
        Assert.That(pub, Does.Not.Contain($"{Subject}.clipboard.req"));
        Assert.That(sub, Does.Not.Contain($"{Subject}.clipboard"));

        // Continua vendo a tela — é o ponto da funcionalidade.
        Assert.That(sub, Does.Contain($"{Subject}.frame"));
    }

    [Test]
    public void LimitedInput_also_blocks_the_input_channel()
    {
        var (pub, _) = Build(RemoteSessionRights.LimitedInput);
        Assert.That(pub, Does.Not.Contain($"{Subject}.input"));
    }

    [Test]
    public void NoTerminal_removes_both_directions_and_completion()
    {
        var (pub, sub) = Build(RemoteSessionRights.NoTerminal);

        Assert.That(pub, Does.Not.Contain($"{Subject}.term.in"));
        Assert.That(pub, Does.Not.Contain($"{Subject}.completion.req"));
        Assert.That(sub, Does.Not.Contain($"{Subject}.term.out"));
        Assert.That(sub, Does.Not.Contain($"{Subject}.completion.res"));

        // Não restringir o resto por acidente.
        Assert.That(pub, Does.Contain($"{Subject}.input"));
        Assert.That(sub, Does.Contain($"{Subject}.frame"));
    }

    [Test]
    public void NoFiles_removes_the_file_channels()
    {
        var (pub, sub) = Build(RemoteSessionRights.NoFiles);

        Assert.That(pub, Does.Not.Contain($"{Subject}.files.req"));
        Assert.That(sub, Does.Not.Contain($"{Subject}.files.resp"));
        Assert.That(sub, Does.Not.Contain($"{Subject}.files.progress"));
        Assert.That(sub, Does.Not.Contain($"{Subject}.files.ready"));
    }

    [Test]
    public void NoDesktop_hides_cursor_but_keeps_frame_stream()
    {
        // Leitura de tela sem revelar a posição do mouse do operador.
        var (_, sub) = Build(RemoteSessionRights.NoDesktop);

        Assert.That(sub, Does.Not.Contain($"{Subject}.cursor"));
        Assert.That(sub, Does.Not.Contain($"{Subject}.cursor.img"));
        Assert.That(sub, Does.Contain($"{Subject}.frame"));
    }

    [Test]
    public void NoEvents_reduces_the_event_stream()
    {
        var (_, sub) = Build(RemoteSessionRights.NoEvents);
        Assert.That(sub, Does.Not.Contain($"{Subject}.event"));
    }

    [Test]
    public void Rights_combine()
    {
        var rights = RemoteSessionRights.ViewOnly | RemoteSessionRights.NoTerminal | RemoteSessionRights.NoFiles;
        var (pub, sub) = Build(rights);

        Assert.That(pub, Does.Not.Contain($"{Subject}.input"));
        Assert.That(pub, Does.Not.Contain($"{Subject}.term.in"));
        Assert.That(pub, Does.Not.Contain($"{Subject}.files.req"));
        Assert.That(sub, Does.Not.Contain($"{Subject}.term.out"));
        Assert.That(sub, Does.Not.Contain($"{Subject}.files.resp"));
        Assert.That(sub, Does.Contain($"{Subject}.frame"));
    }

    [Test]
    public void Control_channel_is_always_preserved_so_liveness_works()
    {
        // Sem .control o viewer não consegue renovar/liveness e o WebSocket cai;
        // restringir capacidades não pode quebrar o sinal de vida.
        var (pub, sub) = Build(RemoteSessionRights.ViewOnly | RemoteSessionRights.NoTerminal
            | RemoteSessionRights.NoFiles | RemoteSessionRights.NoDesktop | RemoteSessionRights.NoEvents);

        Assert.That(pub, Does.Contain($"{Subject}.control"));
        Assert.That(sub, Does.Contain($"{Subject}.control"));
        Assert.That(pub, Does.Contain($"{Subject}.ack"));
    }

    [Test]
    public void Restricted_subjects_never_leak_into_the_other_list()
    {
        // Remover só do pub deixaria o viewer recebendo dados que não deveria;
        // remover só do sub faria o viewer operar e falhar com -ERR.
        var (pub, sub) = Build(RemoteSessionRights.ViewOnly | RemoteSessionRights.NoTerminal | RemoteSessionRights.NoFiles);
        var all = pub.Concat(sub).ToArray();

        Assert.That(all, Does.Not.Contain($"{Subject}.input"));
        Assert.That(all, Does.Not.Contain($"{Subject}.term.in"));
        Assert.That(all, Does.Not.Contain($"{Subject}.term.out"));
        Assert.That(all, Does.Not.Contain($"{Subject}.files.req"));
        Assert.That(all, Does.Not.Contain($"{Subject}.files.resp"));
    }
}
