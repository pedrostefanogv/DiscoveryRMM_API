using System.Text.Json;
using Discovery.Api.Services;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Comando genérico de energia (CommandType.PowerAction): contrato wire,
/// validação do payload e classificação do resultado. Verbos expostos:
/// "restart-firmware" (setup do firmware/BIOS, exclusivo de UEFI),
/// "restart-advanced" (Opções Avançadas de inicialização / WinRE), "cancel"
/// (cancela energia pendente) e "lock" (bloqueia a sessão).
/// </summary>
[TestFixture]
public class PowerActionCommandTests
{
    [Test]
    public void PowerAction_MapsToWireValue_AndIsSpecial()
    {
        Assert.That(CommandTypeWireMapper.ToWireValue(CommandType.PowerAction), Is.EqualTo("poweraction"));
        Assert.That(CommandTypeWireMapper.IsSpecialCommand(CommandType.PowerAction), Is.True);
    }

    [Test]
    public void Validator_AcceptsRestartFirmwareWithZeroDelay()
    {
        var validator = new SpecialCommandPayloadValidator();

        const string payload = """
            { "action": "restart-firmware", "delaySeconds": 0, "force": true, "notifyUser": false, "message": "" }
            """;

        var ok = validator.TryNormalize(CommandType.PowerAction, payload, out var normalizedPayload, out var error);

        Assert.That(ok, Is.True, error);
        using var json = JsonDocument.Parse(normalizedPayload);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("action").GetString(), Is.EqualTo("restart-firmware"));
            Assert.That(json.RootElement.GetProperty("delaySeconds").GetInt32(), Is.EqualTo(0));
            Assert.That(json.RootElement.GetProperty("force").GetBoolean(), Is.True);
            Assert.That(json.RootElement.GetProperty("notifyUser").GetBoolean(), Is.False);
        });
    }

    [TestCase("restart-firmware")]
    [TestCase("restart-advanced")]
    [TestCase("cancel")]
    [TestCase("lock")]
    public void Validator_AcceptsEveryExposedVerb(string action)
    {
        var validator = new SpecialCommandPayloadValidator();

        // delaySeconds: 0 é aceito (execução imediata) — o atraso dos verbos
        // diretos é opcional e o agent decide o fluxo de cada um.
        var payload = $$"""{ "action": "{{action}}", "delaySeconds": 0 }""";

        var ok = validator.TryNormalize(CommandType.PowerAction, payload, out var normalizedPayload, out var error);

        Assert.That(ok, Is.True, error);
        using var json = JsonDocument.Parse(normalizedPayload);
        Assert.That(json.RootElement.GetProperty("action").GetString(), Is.EqualTo(action));
    }

    [Test]
    public void Validator_RejectsUnknownVerb()
    {
        var validator = new SpecialCommandPayloadValidator();

        // "hibernate" ainda NÃO é um verbo exposto (Fase 2): precisa continuar
        // sendo recusado até existir implementação no agent.
        const string payload = """
            { "action": "hibernate" }
            """;

        var ok = validator.TryNormalize(CommandType.PowerAction, payload, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("restart-advanced"));
    }

    [Test]
    public void Validator_RejectsNegativeDelay()
    {
        var validator = new SpecialCommandPayloadValidator();

        const string payload = """
            { "action": "restart-firmware", "delaySeconds": -1 }
            """;

        var ok = validator.TryNormalize(CommandType.PowerAction, payload, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("delaySeconds"));
    }

    [Test]
    public void Classifier_TreatsPowerActionAsManualPower()
    {
        // Power manual: um deferral de janela devolvido pelo agente é inválido
        // e vira Failed (visível), nunca Deferred (beco sem saída silencioso).
        var result = PowerCommandResultClassifier.Classify(
            0,
            "{\"status\":\"deferred_to_window\",\"nextRunAt\":\"2026-11-21T05:00:00Z\"}",
            CommandType.PowerAction);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Failed));
    }

    [Test]
    public void RedeliveryGuard_FlagsPowerAction()
    {
        Assert.That(CommandRedeliveryGuard.IsPowerCommand(CommandType.PowerAction), Is.True);
    }
}
