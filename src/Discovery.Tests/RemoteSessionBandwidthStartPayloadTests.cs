using Discovery.Api.Services;
using Discovery.Core.Configuration;

namespace Discovery.Tests;

/// <summary>
/// Testes do teto (kbps) que o START publica no bloco `bandwidth` para o gate do
/// agente (<see cref="RemoteSessionDispatcher.ResolveStartCeilingKbps"/>).
///
/// CONTEXTO DO BUG (2026-11-10): o START mandava ManualDefaultKbps como teto em
/// QUALQUER modo. Com DefaultMode="auto" (o default do servidor) a sessao que
/// NASCIA em auto nunca usava AutoStartKbps — config morta. E como o agente faz
/// SetMode(auto, c), esse valor virava o teto inicial da escada automatica "por
/// coincidencia" (ManualDefaultKbps e AutoStartKbps sao ambos 8000 por padrao),
/// o que mascarava o defeito. Os testes abaixo usam valores DIFERENTES de proposito
/// para que a coincidencia nao possa esconder uma regressao.
///
/// O modo auto tambem e o caminho que o usuario alterna na janela (HD <-> Auto):
/// o RUNTIME ja foi corrigido para enviar AutoStartKbps; estes testes garantem que
/// o START parta do MESMO valor, para "entrar em auto" e "trocar para auto"
/// convergirem.
/// </summary>
public class RemoteSessionBandwidthStartPayloadTests
{
    private static RemoteAccessBandwidthOptions Options(
        string defaultMode,
        int manualDefaultKbps = 7000,
        int autoStartKbps = 9000,
        int autoMinKbps = 1000,
        int autoMaxKbps = 30000,
        int serverMaxKbps = 0)
        => new()
        {
            DefaultMode = defaultMode,
            ManualDefaultKbps = manualDefaultKbps,
            AutoStartKbps = autoStartKbps,
            AutoMinKbps = autoMinKbps,
            AutoMaxKbps = autoMaxKbps,
            ServerMaxKbps = serverMaxKbps,
        };

    [Test]
    public void Start_ModoAuto_UsaAutoStartKbps_NaoManualDefault()
    {
        // 7000 != 9000: antes do fix este teste devolvia 7000 (ManualDefaultKbps).
        var ceiling = RemoteSessionDispatcher.ResolveStartCeilingKbps(Options("auto"));

        Assert.That(ceiling, Is.EqualTo(9000), "o START em auto deve partir de AutoStartKbps");
        Assert.That(ceiling, Is.Not.EqualTo(7000), "ManualDefaultKbps NAO deve vazar para o modo auto");
    }

    [Test]
    public void Start_ModoAuto_IgnoraCaixaEEspacosDoModo()
    {
        // DefaultMode vem de configuracao: " AUTO " deve ser tratado como auto.
        var ceiling = RemoteSessionDispatcher.ResolveStartCeilingKbps(Options("  AUTO  "));

        Assert.That(ceiling, Is.EqualTo(9000));
    }

    [Test]
    public void Start_ModoManual_UsaManualDefaultKbps()
    {
        var ceiling = RemoteSessionDispatcher.ResolveStartCeilingKbps(Options("manual"));

        Assert.That(ceiling, Is.EqualTo(7000));
    }

    [Test]
    public void Start_ModoHd_NaoTemTetoProprio()
    {
        // HD nao tem teto proprio: 0 = sem limite (o ServerMaxKbps, se houver,
        // continua sendo o teto absoluto aplicado no proprio agente).
        var ceiling = RemoteSessionDispatcher.ResolveStartCeilingKbps(Options("hd"));

        Assert.That(ceiling, Is.Zero);
    }

    [Test]
    public void Start_AutoStartAcimaDoAutoMax_ClampaNoAutoMax()
    {
        // Config incoerente: AutoStartKbps acima de AutoMaxKbps.
        var ceiling = RemoteSessionDispatcher.ResolveStartCeilingKbps(
            Options("auto", autoStartKbps: 50000, autoMaxKbps: 30000));

        Assert.That(ceiling, Is.EqualTo(30000));
    }

    [Test]
    public void Start_AutoStartAbaixoDoAutoMin_ClampaNoAutoMin()
    {
        var ceiling = RemoteSessionDispatcher.ResolveStartCeilingKbps(
            Options("auto", autoStartKbps: 500, autoMinKbps: 1000));

        Assert.That(ceiling, Is.EqualTo(1000));
    }

    [Test]
    public void Start_AutoMinMaiorQueAutoMax_NaoLanca()
    {
        // Config errada (min > max): Math.Clamp lancaria ArgumentException com as
        // bordas invertidas. O metodo ordena o range antes.
        Assert.DoesNotThrow(() =>
            RemoteSessionDispatcher.ResolveStartCeilingKbps(
                Options("auto", autoMinKbps: 8000, autoMaxKbps: 2000)));

        var ceiling = RemoteSessionDispatcher.ResolveStartCeilingKbps(
            Options("auto", autoMinKbps: 8000, autoMaxKbps: 2000));
        Assert.That(ceiling, Is.EqualTo(8000));
    }

    [Test]
    public void Start_ServerMaxKbps_CapaTodosOsModos()
    {
        // ServerMaxKbps=4000 e o teto ABSOLUTO: capa manual, auto e hd.
        Assert.That(
            RemoteSessionDispatcher.ResolveStartCeilingKbps(
                Options("manual", manualDefaultKbps: 7000, serverMaxKbps: 4000)),
            Is.EqualTo(4000), "manual");

        Assert.That(
            RemoteSessionDispatcher.ResolveStartCeilingKbps(
                Options("auto", autoStartKbps: 9000, serverMaxKbps: 4000)),
            Is.EqualTo(4000), "auto");

        // Em hd o teto proprio e 0 (sem limite proprio), entao o cap do servidor
        // nao muda esse 0 — quem impoe o limite absoluto no agente e o
        // serverMaxKbps do proprio bloco.
        Assert.That(
            RemoteSessionDispatcher.ResolveStartCeilingKbps(
                Options("hd", serverMaxKbps: 4000)),
            Is.Zero, "hd");
    }

    [Test]
    public void Start_AutoStartDentroDoServerMax_PassaIntacto()
    {
        var ceiling = RemoteSessionDispatcher.ResolveStartCeilingKbps(
            Options("auto", autoStartKbps: 3000, serverMaxKbps: 4000));

        Assert.That(ceiling, Is.EqualTo(3000));
    }

    [Test]
    public void Start_ModoDesconhecido_CaiNoAuto()
    {
        // Modo desconhecido na config nao pode quebrar o start: cai no ramo auto,
        // que e o comportamento historico do gate (default seguro).
        var ceiling = RemoteSessionDispatcher.ResolveStartCeilingKbps(Options("turbo"));

        Assert.That(ceiling, Is.EqualTo(9000));
    }

    [Test]
    public void Start_AutoStartNaoChegaAoAgenteForaDoAuto()
    {
        // Guarda de regressao: em manual o valor publicado e o MANUAL, nunca o auto.
        Assert.That(
            RemoteSessionDispatcher.ResolveStartCeilingKbps(
                Options("manual", manualDefaultKbps: 7000, autoStartKbps: 9000)),
            Is.EqualTo(7000));
    }
}
