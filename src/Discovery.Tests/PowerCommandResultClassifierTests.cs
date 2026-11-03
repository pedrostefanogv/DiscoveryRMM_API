using Discovery.Core.Enums;
using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// Classificação do output dos comandos de energia. Regressão do bug em que a
/// confirmação antecipada (ExitCode=0, "comando de energia aceito") fazia um
/// restart adiado/negado aparecer como sucesso no painel.
/// </summary>
[TestFixture]
public class PowerCommandResultClassifierTests
{
    [Test]
    public void Executed_Json_IsCompleted()
    {
        var result = PowerCommandResultClassifier.Classify(
            0, "{\"status\":\"executed\",\"action\":\"restart\",\"message\":\"reiniciando em 15s\"}");

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Completed));
        Assert.That(result.StatusCode, Is.EqualTo("executed"));
        Assert.That(result.NextRunAt, Is.Null);
    }

    [Test]
    public void Scheduled_Json_IsDeferredTerminal_SoItIsNotRedelivered()
    {
        var result = PowerCommandResultClassifier.Classify(
            0, "{\"status\":\"scheduled\",\"action\":\"restart\",\"delaySeconds\":300,\"message\":\"agendado\"}");

        // Deferred (terminal): "aceito, executa depois". Running reabriria
        // reentrega/expiração e o comando voltaria a ser entregue antes do
        // timer interno disparar (reboot duplicado).
        Assert.That(result.Status, Is.EqualTo(CommandStatus.Deferred),
            "aceito/agendado não é execução concluída, mas precisa ser terminal");
        Assert.That(result.StatusCode, Is.EqualTo("scheduled"));
        Assert.That(CommandStatusTransitions.IsTerminal(result.Status), Is.True);
    }

    [Test]
    public void ExecutedStatus_WithNonZeroExitCode_IsFailed()
    {
        // Contradição entre o contrato e o exit code real: o exit code manda.
        var result = PowerCommandResultClassifier.Classify(
            1, "{\"status\":\"executed\",\"action\":\"restart\",\"message\":\"ok\"}");

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Failed));
    }

    [Test]
    public void DeferredByUser_Json_IsDeferred()
    {
        var result = PowerCommandResultClassifier.Classify(
            0, "{\"status\":\"deferred_by_user\",\"action\":\"restart\",\"defer\":{\"current\":1,\"max\":3,\"minutes\":60}}");

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Deferred));
        Assert.That(result.StatusCode, Is.EqualTo("deferred_by_user"));
    }

    [Test]
    public void DeferredToWindow_Json_IsDeferredWithNextRunAt()
    {
        var result = PowerCommandResultClassifier.Classify(
            0, "{\"status\":\"deferred_to_window\",\"action\":\"restart\",\"nextRunAt\":\"2026-11-03T05:00:00Z\",\"message\":\"fora da janela\"}");

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Deferred));
        Assert.That(result.StatusCode, Is.EqualTo("deferred_to_window"));
        Assert.That(result.NextRunAt, Is.EqualTo("2026-11-03T05:00:00Z"));
    }

    [Test]
    public void Denied_Json_IsCancelled_NotASuccess()
    {
        var result = PowerCommandResultClassifier.Classify(
            10, "{\"status\":\"denied\",\"action\":\"restart\",\"message\":\"usuário negou a reinicialização\"}");

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Cancelled));
        Assert.That(result.StatusCode, Is.EqualTo("denied"));
    }

    [Test]
    public void Failed_Json_IsFailed()
    {
        var result = PowerCommandResultClassifier.Classify(
            1, "{\"status\":\"failed\",\"action\":\"shutdown\",\"message\":\"seShutdownPrivilege ausente\"}");

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Failed));
        Assert.That(result.StatusCode, Is.EqualTo("failed"));
    }

    [Test]
    public void LegacyText_DeferredToWindow_IsDeferred_AndExtractsNextRunAt()
    {
        // Formato real de produção (agente antigo, texto livre + nextRunAt=...).
        var result = PowerCommandResultClassifier.Classify(
            0, "comando de energia aceito; deferred_to_window nextRunAt=2026-11-03T05:00:00Z");

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Deferred));
        Assert.That(result.StatusCode, Is.EqualTo("deferred_to_window"));
        Assert.That(result.NextRunAt, Is.EqualTo("2026-11-03T05:00:00Z"));
    }

    [Test]
    public void LegacyText_IsCaseInsensitive()
    {
        var result = PowerCommandResultClassifier.Classify(0, "DEFERRED_TO_WINDOW NEXTRUNAT=2026-11-03T05:00:00Z");

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Deferred));
        Assert.That(result.NextRunAt, Is.EqualTo("2026-11-03T05:00:00Z"));
    }

    [Test]
    public void LegacyText_DeferidoPeloUsuario_IsDeferredByUser()
    {
        var result = PowerCommandResultClassifier.Classify(0, "Reinício adiado pelo usuário. Nada executado.");

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Deferred));
        Assert.That(result.StatusCode, Is.EqualTo("deferred_by_user"));
    }

    [Test]
    public void LegacyText_AgendadoSilenciosamente_IsScheduled()
    {
        var result = PowerCommandResultClassifier.Classify(0, "Desligamento agendado silenciosamente para 300s.");

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Deferred));
        Assert.That(result.StatusCode, Is.EqualTo("scheduled"));
    }

    [Test]
    public void PlainSuccessText_IsCompleted()
    {
        var result = PowerCommandResultClassifier.Classify(0, "comando de energia aceito");

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Completed));
        Assert.That(result.StatusCode, Is.Null, "texto sem contrato não inventa status code");
    }

    [Test]
    public void PlainFailureText_IsFailed()
    {
        var result = PowerCommandResultClassifier.Classify(1, "erro inesperado");

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Failed));
        Assert.That(result.StatusCode, Is.Null);
    }

    [Test]
    public void NullOrEmptyOutput_FallsBackToExitCode()
    {
        Assert.That(PowerCommandResultClassifier.Classify(0, null).Status, Is.EqualTo(CommandStatus.Completed));
        Assert.That(PowerCommandResultClassifier.Classify(0, "   ").Status, Is.EqualTo(CommandStatus.Completed));
        Assert.That(PowerCommandResultClassifier.Classify(1, null).Status, Is.EqualTo(CommandStatus.Failed));
    }

    [Test]
    public void JsonEmbeddedInLogText_IsStillParsed()
    {
        var result = PowerCommandResultClassifier.Classify(
            0, "result: {\"status\":\"deferred_to_window\",\"nextRunAt\":\"2026-11-03T05:00:00Z\"} (agent 1.2.3)");

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Deferred));
        Assert.That(result.NextRunAt, Is.EqualTo("2026-11-03T05:00:00Z"));
    }

    [Test]
    public void UnknownJsonStatus_FallsBackToExitCode()
    {
        var ok = PowerCommandResultClassifier.Classify(0, "{\"status\":\"ok\",\"message\":\"fine\"}");
        var failed = PowerCommandResultClassifier.Classify(2, "{\"status\":\"ok\"}");

        Assert.That(ok.Status, Is.EqualTo(CommandStatus.Completed));
        Assert.That(ok.StatusCode, Is.Null);
        Assert.That(failed.Status, Is.EqualTo(CommandStatus.Failed));
    }

    [Test]
    public void MalformedJson_FallsBackToLegacyTextClassification()
    {
        var result = PowerCommandResultClassifier.Classify(0, "{\"status\": deferred_to_window nextRunAt=2026-11-03T05:00:00Z");

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Deferred));
        Assert.That(result.NextRunAt, Is.EqualTo("2026-11-03T05:00:00Z"));
    }

    [Test]
    public void DeferredIsNotExecution()
    {
        Assert.That(PowerCommandResultClassifier.IsNonExecution(CommandStatus.Deferred), Is.True);
        Assert.That(PowerCommandResultClassifier.IsNonExecution(CommandStatus.Cancelled), Is.True);
        Assert.That(PowerCommandResultClassifier.IsNonExecution(CommandStatus.Failed), Is.True);
        Assert.That(PowerCommandResultClassifier.IsNonExecution(CommandStatus.Completed), Is.False);
    }
}
