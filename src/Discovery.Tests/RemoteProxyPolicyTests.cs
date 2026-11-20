using Discovery.Core.Configuration;

namespace Discovery.Tests;

/// <summary>
/// Testes da allowlist de proxy herdável (RemoteProxyPolicy).
///
/// O foco é FAIL-CLOSED: qualquer JSON ausente, vazio ou corrompido tem de cair
/// em allowlist vazia (bloqueio total), nunca em modo "open". Um bug aqui
/// abriria o proxy de toda a frota para a rede interna dos clientes — é o
/// controle de segurança mais sensível desta feature.
/// </summary>
[TestFixture]
public class RemoteProxyPolicyTests
{
    // ── Fail-closed: o caso mais importante ──────────────────────────────

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("{}")]
    public void Parse_Vazio_CaiEmBloqueioTotal(string? json)
    {
        var policy = RemoteProxyPolicy.Parse(json);

        Assert.That(policy.Mode, Is.EqualTo(RemoteProxyPolicy.ModeAllowlist));
        Assert.That(policy.Entries, Is.Empty);
        Assert.That(policy.IsOpen, Is.False);
    }

    [TestCase("{invalido")]
    [TestCase("[1,2,3]")]
    [TestCase("\"apenas texto\"")]
    [TestCase("null")]
    [TestCase("42")]
    public void Parse_JsonCorrompido_NuncaViraOpen(string json)
    {
        var policy = RemoteProxyPolicy.Parse(json);

        Assert.That(policy.IsOpen, Is.False, "JSON corrompido jamais pode liberar o proxy");
        Assert.That(policy.Mode, Is.EqualTo(RemoteProxyPolicy.ModeAllowlist));
        Assert.That(policy.Entries, Is.Empty);
    }

    [Test]
    public void Parse_ModoDesconhecido_CaiEmAllowlist()
    {
        var policy = RemoteProxyPolicy.Parse("""{"mode":"qualquer-coisa"}""");

        Assert.That(policy.Mode, Is.EqualTo(RemoteProxyPolicy.ModeAllowlist));
        Assert.That(policy.IsOpen, Is.False);
    }

    // ── Modo open ────────────────────────────────────────────────────────

    [TestCase("open")]
    [TestCase("OPEN")]
    [TestCase(" Open ")]
    public void Parse_ModoOpen_ReconhecidoCaseInsensitive(string mode)
    {
        var policy = RemoteProxyPolicy.Parse($$"""{"mode":"{{mode}}"}""");

        Assert.That(policy.IsOpen, Is.True);
    }

    // ── Entradas ─────────────────────────────────────────────────────────

    [Test]
    public void Parse_LeEntradasComPortasENota()
    {
        var policy = RemoteProxyPolicy.Parse("""
            {
              "mode": "allowlist",
              "entries": [
                { "cidr": "192.168.10.0/24", "ports": [8006, 22], "note": "Proxmox matriz" },
                { "cidr": "10.0.0.5", "ports": [] }
              ]
            }
            """);

        Assert.That(policy.Entries, Has.Count.EqualTo(2));
        Assert.That(policy.Entries[0].Cidr, Is.EqualTo("192.168.10.0/24"));
        Assert.That(policy.Entries[0].Ports, Is.EqualTo(new[] { 22, 8006 }));
        Assert.That(policy.Entries[0].Note, Is.EqualTo("Proxmox matriz"));
        Assert.That(policy.Entries[1].Cidr, Is.EqualTo("10.0.0.5"));
        Assert.That(policy.Entries[1].Ports, Is.Empty);
    }

    [Test]
    public void EffectiveAllowlist_DevolveApenasOsCidrs()
    {
        var policy = RemoteProxyPolicy.Parse("""
            {"entries":[{"cidr":"192.168.10.0/24","ports":[8006]},{"cidr":"10.0.0.5"}]}
            """);

        Assert.That(policy.EffectiveAllowlist, Is.EqualTo(new[] { "192.168.10.0/24", "10.0.0.5" }));
    }

    [Test]
    public void Parse_DescartaEntradasSemCidr()
    {
        var policy = RemoteProxyPolicy.Parse("""
            {"entries":[{"cidr":"192.168.10.0/24"},{"cidr":"   "},{"ports":[80]}]}
            """);

        Assert.That(policy.Entries, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_DescartaPortasForaDaFaixa()
    {
        var policy = RemoteProxyPolicy.Parse("""
            {"entries":[{"cidr":"10.0.0.1","ports":[22,99999,0,-5,80]}]}
            """);

        Assert.That(policy.Entries[0].Ports, Is.EqualTo(new[] { 22, 80 }));
    }

    [Test]
    public void Parse_DeduplicaPortas()
    {
        var policy = RemoteProxyPolicy.Parse("""
            {"entries":[{"cidr":"10.0.0.1","ports":[22,22,80,80]}]}
            """);

        Assert.That(policy.Entries[0].Ports, Is.EqualTo(new[] { 22, 80 }));
    }

    [Test]
    public void Parse_LeAllowLoopbackEMaxResponseBytes()
    {
        var policy = RemoteProxyPolicy.Parse("""
            {"allowLoopback":true,"maxResponseBytes":2048}
            """);

        Assert.That(policy.AllowLoopback, Is.True);
        Assert.That(policy.MaxResponseBytes, Is.EqualTo(2048));
    }

    [Test]
    public void Parse_MaxResponseBytesNegativo_ViraZero()
    {
        var policy = RemoteProxyPolicy.Parse("""{"maxResponseBytes":-1}""");

        Assert.That(policy.MaxResponseBytes, Is.EqualTo(0));
    }

    // ── Serialização ─────────────────────────────────────────────────────

    [Test]
    public void RoundTrip_PreservaPolitica()
    {
        var original = new RemoteProxyPolicy
        {
            Mode = RemoteProxyPolicy.ModeAllowlist,
            Entries = [new RemoteProxyEntry { Cidr = "192.168.10.0/24", Ports = [22, 8006], Note = "Proxmox" }],
            AllowLoopback = true,
            AllowedPorts = [443],
            MaxResponseBytes = 4096
        };

        var restored = RemoteProxyPolicy.Parse(original.ToJson());

        Assert.That(restored.Mode, Is.EqualTo(original.Mode));
        Assert.That(restored.EffectiveAllowlist, Is.EqualTo(original.EffectiveAllowlist));
        Assert.That(restored.Entries[0].Ports, Is.EqualTo(new[] { 22, 8006 }));
        Assert.That(restored.AllowLoopback, Is.True);
        Assert.That(restored.AllowedPorts, Is.EqualTo(new[] { 443 }));
        Assert.That(restored.MaxResponseBytes, Is.EqualTo(4096));
    }

    // ── Validação estrita (PATCH de configuração) ────────────────────────

    [Test]
    public void TryParseStrict_AceitaPoliticaValida()
    {
        var ok = RemoteProxyPolicy.TryParseStrict(
            """{"entries":[{"cidr":"192.168.10.0/24","ports":[8006]}]}""",
            out var policy, out var error);

        Assert.That(ok, Is.True);
        Assert.That(error, Is.Null);
        Assert.That(policy.Entries, Has.Count.EqualTo(1));
    }

    [Test]
    public void TryParseStrict_RecusaJsonInvalido()
    {
        var ok = RemoteProxyPolicy.TryParseStrict("{nao-e-json", out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("inválido"));
    }

    [Test]
    public void TryParseStrict_RecusaEntradaSemCidr()
    {
        var ok = RemoteProxyPolicy.TryParseStrict("""{"entries":[{"ports":[80]}]}""", out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("cidr"));
    }

    [Test]
    public void TryParseStrict_RecusaPortaForaDaFaixa()
    {
        var ok = RemoteProxyPolicy.TryParseStrict(
            """{"entries":[{"cidr":"10.0.0.1","ports":[99999]}]}""", out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("65535"));
    }

    [Test]
    public void TryParseStrict_VazioEhValido()
    {
        var ok = RemoteProxyPolicy.TryParseStrict("", out var policy, out var error);

        Assert.That(ok, Is.True);
        Assert.That(error, Is.Null);
        Assert.That(policy.Entries, Is.Empty);
    }

    // ── IsUnconfigured: distingue "não configurado" de "configurado vazio" ──

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("{}")]
    [TestCase("null")]
    [TestCase("{ }")]
    public void IsUnconfigured_ReconheceDefaultDaMigration(string? json)
    {
        // A coluna é NOT NULL DEFAULT '{}', então este é o valor que o handler
        // recebe quando nenhum escopo definiu política — e é o gatilho do
        // fallback para o appsettings.
        Assert.That(RemoteProxyPolicy.IsUnconfigured(json), Is.True);
    }

    [TestCase("""{"mode":"allowlist"}""")]
    [TestCase("""{"entries":[]}""")]
    [TestCase("""{"mode":"open"}""")]
    [TestCase("""{"entries":[{"cidr":"10.0.0.0/8"}]}""")]
    public void IsUnconfigured_ReconhecePoliticaExplicita(string json)
    {
        // Qualquer documento salvo pela UI tem ao menos "mode", então conta como
        // configuração explícita — e o handler passa a usar o banco.
        Assert.That(RemoteProxyPolicy.IsUnconfigured(json), Is.False);
    }

    [TestCase("{nao-e-json")]
    [TestCase("[1,2,3]")]
    [TestCase("42")]
    public void IsUnconfigured_NaoTrataJsonCorrompidoComoNaoConfigurado(string json)
    {
        // Se um JSON corrompido fosse tratado como "não configurado", o handler
        // cairia no appsettings e IGNORARIA a corrupção — em vez de bloquear
        // tudo, que é o comportamento fail-closed correto.
        Assert.That(RemoteProxyPolicy.IsUnconfigured(json), Is.False);
    }

    // ── Portas efetivas (restrição de sessão) ────────────────────────────

    [Test]
    public void EffectiveAllowedPorts_UnePortasDasEntradasEAsGlobais()
    {
        var policy = RemoteProxyPolicy.Parse("""
            {
              "entries":[
                {"cidr":"192.168.10.0/24","ports":[8006,22]},
                {"cidr":"10.0.0.0/8","ports":[443]}
              ],
              "allowedPorts":[8080]
            }
            """);

        // O agent tem UM conjunto de portas por sessão, então a união é o que
        // de fato restringe. Sem isto, declarar [8006] numa entrada liberaria
        // TODAS as portas (o gate do agent é pulado quando o conjunto é vazio).
        Assert.That(policy.EffectiveAllowedPorts, Is.EqualTo(new[] { 22, 443, 8006, 8080 }));
    }

    [Test]
    public void EffectiveAllowedPorts_VazioQuandoNinguemDeclaraPorta()
    {
        var policy = RemoteProxyPolicy.Parse("""
            {"entries":[{"cidr":"192.168.10.0/24"}]}
            """);

        // Vazio = todas as portas. É o default, e a UI precisa avisar disso.
        Assert.That(policy.EffectiveAllowedPorts, Is.Empty);
    }

    [Test]
    public void EffectiveAllowedPorts_DeduplicaEntreEntradas()
    {
        var policy = RemoteProxyPolicy.Parse("""
            {"entries":[{"cidr":"10.0.0.1","ports":[22]},{"cidr":"10.0.0.2","ports":[22,80]}]}
            """);

        Assert.That(policy.EffectiveAllowedPorts, Is.EqualTo(new[] { 22, 80 }));
    }

    [Test]
    public void BlockAll_EhBloqueioTotal()
    {
        var policy = RemoteProxyPolicy.BlockAll();

        Assert.That(policy.IsOpen, Is.False);
        Assert.That(policy.Entries, Is.Empty);
        Assert.That(policy.AllowLoopback, Is.False);
    }
}
