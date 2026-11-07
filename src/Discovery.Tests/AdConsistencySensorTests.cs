using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Services;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// F5 — SensorType.ADConsistency (19).
///
/// O agente Go mede consistência de AD (repadmin/dcdiag/w32tm) como SYSTEM, sem
/// credencial de domínio, e publica a evidência em canais. O SERVIDOR decide
/// status/severidade — o agente nunca recebe limiar.
///
/// A regra central desta suíte é a mesma do isServer null: ausência de
/// evidência NÃO é evidência de saúde. Quando o agente não conseguiu consultar
/// o domínio (ad_collect_ok = 0), o resultado tem de ser Unknown — nunca
/// Passing — senão um DC que nunca foi verificado apareceria verde.
/// </summary>
public class AdConsistencySensorTests
{
    private static SensorDefinition Def(
        double? warning = null,
        double? critical = null,
        SensorSeverity severityWhenFailing = SensorSeverity.Critical)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "Consistencia AD",
            SensorType = SensorType.ADConsistency,
            WarningThreshold = warning,
            CriticalThreshold = critical,
            Comparison = SensorComparison.HigherIsWorse,
            SeverityWhenFailing = severityWhenFailing,
            TimeoutSeconds = 120,
            IsActive = true
        };

    private static SensorEvaluation Eval(
        SensorDefinition definition,
        Dictionary<string, double>? channels = null,
        AgentSensorMeasureDto? measure = null,
        string? error = null)
        => SensorEvaluator.Evaluate(
            definition,
            measure ?? new AgentSensorMeasureDto(),
            error,
            null,
            null,
            channels);

    /// <summary>Payload saudável: coletou tudo e nada falhou.</summary>
    private static Dictionary<string, double> Saudavel() => new()
    {
        ["ad_collect_ok"] = 1,
        ["replication_failures"] = 0,
        ["replication_ok"] = 1,
        ["dcdiag_failed_tests"] = 0,
        ["dcdiag_ok"] = 1,
        ["time_offset_seconds"] = 0.5,
        ["clock_ok"] = 1
    };

    // ─────────────────────────────────────────────────────────────────────
    // Enum / contrato
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void TipoExisteComValor19()
    {
        Assert.That((int)SensorType.ADConsistency, Is.EqualTo(19));
    }

    /// <summary>
    /// O enum precisa continuar contíguo a partir de 0 e NÃO pode ter reordenado
    /// nada: o agente mapeia tipo por NÚMERO (sensors_types.go: sensorTypeByNumber).
    /// Um valor deslocado faria o agente coletar o sensor errado em silêncio.
    /// </summary>
    [Test]
    public void EnumContinuaContiguoERetrocompativel()
    {
        var valores = Enum.GetValues<SensorType>().Select(v => (int)v).OrderBy(v => v).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(valores, Is.EqualTo(Enumerable.Range(0, valores.Length).ToArray()),
                "o enum precisa ser contíguo de 0 a N");
            // Contratos já publicados com o agente — NÃO podem mudar de número.
            Assert.That((int)SensorType.DiskSpace, Is.EqualTo(0));
            Assert.That((int)SensorType.Script, Is.EqualTo(13));
            Assert.That((int)SensorType.CertificateExpiry, Is.EqualTo(16));
            Assert.That((int)SensorType.DefenderStatus, Is.EqualTo(17));
            Assert.That((int)SensorType.BitLocker, Is.EqualTo(18));
            // O novo é o ÚLTIMO (aditivo).
            Assert.That(valores[^1], Is.EqualTo(19));
        });
    }

    [Test]
    public void CatalogoTemCanaisComUmPrimario()
    {
        var canais = SensorChannelCatalog.Resolve(new SensorDefinition { SensorType = SensorType.ADConsistency });

        Assert.That(canais, Is.Not.Empty);
        Assert.That(canais.Count(c => c.IsPrimary), Is.EqualTo(1));
        Assert.That(canais.Single(c => c.IsPrimary).Key, Is.EqualTo("replication_failures"));

        // Todos os canais que o agente publica precisam ter rótulo no catálogo.
        var chaves = canais.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var esperado in new[]
                 {
                     "ad_collect_ok", "replication_failures", "replication_ok",
                     "dcdiag_failed_tests", "dcdiag_ok", "time_offset_seconds", "clock_ok"
                 })
        {
            Assert.That(chaves, Does.Contain(esperado), $"canal {esperado} sem entrada no catálogo");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // REGRA CENTRAL: coleta falhada NUNCA vira OK
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O caso do enunciado: ad_collect_ok = false. NÃO pode ser Passing.
    /// </summary>
    [Test]
    public void ColetaFalhada_NaoViraPassing()
    {
        var canais = new Dictionary<string, double> { ["ad_collect_ok"] = 0 };

        var resultado = Eval(Def(), canais);

        Assert.Multiple(() =>
        {
            Assert.That(resultado.Status, Is.Not.EqualTo(SensorStatus.Passing),
                "coleta falhada reportada como OK mascararia um DC não verificado");
            Assert.That(resultado.Status, Is.EqualTo(SensorStatus.Unknown));
            Assert.That(resultado.Severity, Is.Null);
            Assert.That(resultado.Message, Does.Contain("não conseguiu consultar"));
        });
    }

    /// <summary>
    /// Mesmo que todos os outros canais pareçam saudáveis: se a coleta não
    /// aconteceu, os valores presentes são de uma fonte parcial e não podem
    /// promover o sensor a verde.
    /// </summary>
    [Test]
    public void ColetaFalhada_ComCanaisSaudaveisAindaEUnknown()
    {
        var canais = new Dictionary<string, double>
        {
            ["ad_collect_ok"] = 0,
            ["replication_failures"] = 0,
            ["dcdiag_failed_tests"] = 0,
            ["clock_ok"] = 1
        };

        var resultado = Eval(Def(), canais);

        Assert.That(resultado.Status, Is.EqualTo(SensorStatus.Unknown));
    }

    /// <summary>Sem o canal de coleta não há como afirmar saúde: Unknown.</summary>
    [Test]
    public void SemCanalDeColeta_EUnknown()
    {
        var resultado = Eval(Def(), new Dictionary<string, double>());

        Assert.Multiple(() =>
        {
            Assert.That(resultado.Status, Is.EqualTo(SensorStatus.Unknown));
            Assert.That(resultado.Message, Does.Contain("não reportou"));
        });
    }

    /// <summary>Agente antigo/payload nulo: Unknown, sem exceção.</summary>
    [Test]
    public void SemCanaisNenhum_EUnknownSemLancar()
    {
        var resultado = Eval(Def(), null);

        Assert.That(resultado.Status, Is.EqualTo(SensorStatus.Unknown));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Coleta OK: avaliação dos blocos
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void ColetaOkSemProblemas_E_Passing()
    {
        var resultado = Eval(Def(), Saudavel());

        Assert.Multiple(() =>
        {
            Assert.That(resultado.Status, Is.EqualTo(SensorStatus.Passing));
            Assert.That(resultado.Severity, Is.Null);
            Assert.That(resultado.Message, Does.Contain("sem problemas"));
        });
    }

    [Test]
    public void FalhasDeReplicacao_E_Failing()
    {
        var canais = Saudavel();
        canais["replication_failures"] = 3;
        canais["replication_ok"] = 0;

        var resultado = Eval(Def(), canais);

        Assert.Multiple(() =>
        {
            Assert.That(resultado.Status, Is.EqualTo(SensorStatus.Failing));
            Assert.That(resultado.Severity, Is.EqualTo(SensorSeverity.Critical));
            Assert.That(resultado.Message, Does.Contain("3 falha(s) de replicação"));
            Assert.That(resultado.PrimaryValue, Is.EqualTo(3));
        });
    }

    [Test]
    public void TestesDcdiagComFalha_E_Warning()
    {
        var canais = Saudavel();
        canais["dcdiag_failed_tests"] = 2;
        canais["dcdiag_ok"] = 0;

        var resultado = Eval(Def(), canais);

        Assert.Multiple(() =>
        {
            Assert.That(resultado.Status, Is.EqualTo(SensorStatus.Warning));
            Assert.That(resultado.Message, Does.Contain("2 teste(s) do dcdiag"));
        });
    }

    /// <summary>Desvio de relógio &gt; 15s quebra Kerberos.</summary>
    [Test]
    public void RelogioForaDeSincronia_E_Warning()
    {
        var canais = Saudavel();
        canais["clock_ok"] = 0;
        canais["time_offset_seconds"] = -42;

        var resultado = Eval(Def(), canais);

        Assert.Multiple(() =>
        {
            Assert.That(resultado.Status, Is.EqualTo(SensorStatus.Warning));
            Assert.That(resultado.Message, Does.Contain("relógio fora de sincronia"));
        });
    }

    /// <summary>Replicação (Failing) vence dcdiag/relógio (Warning) no ranking.</summary>
    [Test]
    public void PiorProblemaDeterminaOStatus()
    {
        var canais = Saudavel();
        canais["replication_failures"] = 1;
        canais["dcdiag_failed_tests"] = 1;
        canais["clock_ok"] = 0;

        var resultado = Eval(Def(), canais);

        Assert.That(resultado.Status, Is.EqualTo(SensorStatus.Failing));
        Assert.That(resultado.Message, Does.Contain("dcdiag"));
        Assert.That(resultado.Message, Does.Contain("relógio"));
    }

    /// <summary>Bloco ausente após coleta bem-sucedida = não avaliado (Warning).</summary>
    [Test]
    public void BlocoNaoAvaliado_E_Warning()
    {
        var canais = new Dictionary<string, double> { ["ad_collect_ok"] = 1 };

        var resultado = Eval(Def(), canais);

        Assert.Multiple(() =>
        {
            Assert.That(resultado.Status, Is.EqualTo(SensorStatus.Warning));
            Assert.That(resultado.Message, Does.Contain("não avaliado"));
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fonte da evidência: campos explícitos vencem, canais são fallback
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void CamposDaMedida_SaoPreferidosSobreCanais()
    {
        var measure = new AgentSensorMeasureDto { AdCollectOk = true, AdReplicationFailures = 0, AdReplicationOk = true };
        // Canal contraditório de propósito: o campo explícito tem de vencer.
        var canais = new Dictionary<string, double> { ["ad_collect_ok"] = 0, ["replication_failures"] = 99 };

        var resultado = Eval(Def(), canais, measure);

        Assert.That(resultado.Status, Is.Not.EqualTo(SensorStatus.Unknown),
            "campo explícito da medida deve prevalecer sobre o canal");
        Assert.That(resultado.Channels["replication_failures"], Is.EqualTo(0));
    }

    /// <summary>
    /// Agente antigo (só Channels) continua funcionando: o fallback preenche.
    /// </summary>
    [Test]
    public void CanaisDoAgente_SaoFallbackQuandoNaoHaCampos()
    {
        var resultado = Eval(Def(), Saudavel(), new AgentSensorMeasureDto());

        Assert.Multiple(() =>
        {
            Assert.That(resultado.Status, Is.EqualTo(SensorStatus.Passing));
            Assert.That(resultado.Channels["ad_collect_ok"], Is.EqualTo(1));
        });
    }

    /// <summary>Erro explícito de coleta do agente continua sendo Unknown.</summary>
    [Test]
    public void ErroDeColetaDoAgente_EUnknown()
    {
        var resultado = Eval(Def(), Saudavel(), null, error: "agente sem permissão para o repadmin");

        Assert.Multiple(() =>
        {
            Assert.That(resultado.Status, Is.EqualTo(SensorStatus.Unknown));
            Assert.That(resultado.Message, Does.Contain("permissão"));
        });
    }

    /// <summary>Timeout de coleta é falha explícita (regra geral do avaliador).</summary>
    [Test]
    public void Timeout_E_Failing()
    {
        var resultado = Eval(Def(), Saudavel(), new AgentSensorMeasureDto { TimedOut = true });

        Assert.That(resultado.Status, Is.EqualTo(SensorStatus.Failing));
    }
}
