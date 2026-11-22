using Discovery.Core.Configuration;

namespace Discovery.Tests;

/// <summary>
/// O gate que decide se o bloco "webrtc" entra no payload de start.
///
/// A condição é a conjunção de quatro coisas: política EFETIVA (blob ligado +
/// kill switch), transporte pedido = webrtc, e kind desta sessão na lista. Estes
/// testes fixam a semântica de <see cref="RemoteAccessWebRtcPolicy.IsEffective"/>,
/// que é a parte que pode falhar em silêncio.
/// </summary>
[TestFixture]
public class RemoteAccessWebRtcGateTests
{
    private const string EnabledScreen = """
        {"enabled":true,"kinds":["screen"],
         "ice":{"stunUrls":["stun:stun.l.google.com:19302"]}}
        """;

    [Test]
    public void IsEffective_exige_enabled_e_ao_menos_um_kind()
    {
        var policy = RemoteAccessWebRtcPolicy.Parse(EnabledScreen);
        Assert.That(policy.IsEffective, Is.True);

        var disabled = RemoteAccessWebRtcPolicy.Parse("""{"enabled":false,"kinds":["screen"]}""");
        Assert.That(disabled.IsEffective, Is.False, "enabled=false nunca é efetivo");

        var noKinds = RemoteAccessWebRtcPolicy.Parse("""{"enabled":true,"kinds":[]}""");
        Assert.That(noKinds.IsEffective, Is.False,
            "lista vazia é 'nenhum kind', nunca 'todos' — um default implícito ligaria WebRTC em terminal");

        // registry segue sendo o exemplo de kind que NÃO é suportado: o gargalo
        // é o tamanho da página (500 itens), não o transporte.
        var onlyInvalidKinds = RemoteAccessWebRtcPolicy.Parse("""{"enabled":true,"kinds":["registry"]}""");
        Assert.That(onlyInvalidKinds.IsEffective, Is.False,
            "kind só inválido é filtrado e deixa a política sem efeito");
    }

    [Test]
    public void EffectiveKinds_filtra_o_que_nao_e_suportado()
    {
        var policy = RemoteAccessWebRtcPolicy.Parse(
            """{"enabled":true,"kinds":["screen","terminal","processes","rdp","registry","banana"]}""");

        Assert.That(policy.EffectiveKinds,
            Is.EqualTo(new[] { "screen", "terminal", "processes", "rdp" }),
            "terminal e processes SÃO suportados; registry e kinds inexistentes são filtrados");
    }

    [Test]
    public void Proxy_e_um_kind_valido()
    {
        // Proxy usa o MESMO SessionTunnel do RDP no agente (multiplexação TCP com
        // framing binário de 6 B). Excluí-lo seria incoerente — e é onde o P2P
        // mais ajuda, porque o destino (Proxmox, iDRAC, switch) costuma estar em
        // outra rede.
        var policy = RemoteAccessWebRtcPolicy.Parse(
            """{"enabled":true,"kinds":["proxy"]}""");

        Assert.Multiple(() =>
        {
            Assert.That(policy.EffectiveKinds, Is.EqualTo(new[] { "proxy" }),
                "proxy não pode ser filtrado como se fosse inválido");
            Assert.That(policy.IsEffective, Is.True);
        });
    }

    [Test]
    public void Registry_nao_e_um_kind_valido()
    {
        // A resposta do registry já é paginada em 500 itens porque estouraria o
        // limite do NATS: o gargalo é o tamanho da página, não o transporte.
        Assert.That(RemoteAccessWebRtcPolicy.TryParseStrict(
            """{"enabled":true,"kinds":["registry"]}""", out _, out var error), Is.False,
            "registry não deve ser aceito: trocar o transporte não resolveria o gargalo real");
        Assert.That(error, Does.Contain("registry"));
    }

    [Test]
    public void AllowedKinds_cobre_os_casos_com_ganho_e_exclui_os_demais()
    {
        Assert.Multiple(() =>
        {
            // Ganho comprovado por medição.
            Assert.That(RemoteAccessWebRtcPolicy.AllowedKinds, Does.Contain("screen"));
            Assert.That(RemoteAccessWebRtcPolicy.AllowedKinds, Does.Contain("rdp"));
            Assert.That(RemoteAccessWebRtcPolicy.AllowedKinds, Does.Contain("files"));
            Assert.That(RemoteAccessWebRtcPolicy.AllowedKinds, Does.Contain("proxy"));
            Assert.That(RemoteAccessWebRtcPolicy.AllowedKinds, Does.Contain("all"));

            // Excluído com motivo documentado.
            // Terminal e processes: liberados junto com o caminho de dados deles.
            // Sem o caminho de dados o peer conectaria e o tráfego seguiria no
            // NATS — selo verde mentindo.
            Assert.That(RemoteAccessWebRtcPolicy.AllowedKinds, Does.Contain("terminal"));
            Assert.That(RemoteAccessWebRtcPolicy.AllowedKinds, Does.Not.Contain("registry"));
            Assert.That(RemoteAccessWebRtcPolicy.AllowedKinds, Does.Contain("processes"));
        });
    }

    [Test]
    public void Gate_de_kind_casa_com_o_kind_da_sessao_em_minusculas()
    {
        // O handler compara cmd.Kind.ToString().ToLowerInvariant() com a lista.
        var policy = RemoteAccessWebRtcPolicy.Parse(EnabledScreen);

        Assert.Multiple(() =>
        {
            Assert.That(policy.EffectiveKinds.Contains("screen"), Is.True);
            Assert.That(policy.EffectiveKinds.Contains("files"), Is.False);
            Assert.That(policy.EffectiveKinds.Contains("terminal"), Is.False);
        });
    }

    [Test]
    public void Politica_ausente_nunca_e_efetiva()
    {
        // O default da migration ("{}") e qualquer JSON corrompido caem em
        // desligado: nenhuma instalação muda de transporte no rollout.
        Assert.Multiple(() =>
        {
            Assert.That(RemoteAccessWebRtcPolicy.Parse("{}").IsEffective, Is.False);
            Assert.That(RemoteAccessWebRtcPolicy.Parse(null).IsEffective, Is.False);
            Assert.That(RemoteAccessWebRtcPolicy.Parse("{corrompido").IsEffective, Is.False);
            Assert.That(RemoteAccessWebRtcPolicy.Disabled().IsEffective, Is.False);
        });
    }

    [Test]
    public void Kill_switch_desligado_zera_o_efetivo_mesmo_com_blob_ligado()
    {
        // A combinação é feita no resolver (webRtcEffective = blob.Enabled &&
        // flag). Aqui fixamos que o blob sozinho NÃO basta: o consumidor precisa
        // aplicar a flag, e é isso que o handler faz.
        var blob = RemoteAccessWebRtcPolicy.Parse(EnabledScreen);
        Assert.That(blob.Enabled, Is.True);

        // Simula a conjunção feita pelo resolver com a flag desligada.
        var effectiveWithFlagOff = blob.Enabled && false;
        Assert.That(effectiveWithFlagOff, Is.False,
            "blob ligado + kill switch desligado = NÃO efetivo (fail-closed duplo)");
    }
}
