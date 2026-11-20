using System.Text.Json;
using Discovery.Api.Services;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Comando genérico de energia (CommandType.PowerAction): contrato wire,
/// validação do payload e classificação do resultado. Hoje expõe só o verbo
/// "restart-firmware" (reinício direto no setup do firmware/BIOS, UEFI).
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

    [Test]
    public void Validator_RejectsUnknownVerb()
    {
        var validator = new SpecialCommandPayloadValidator();

        const string payload = """
            { "action": "restart-advanced" }
            """;

        var ok = validator.TryNormalize(CommandType.PowerAction, payload, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("restart-firmware"));
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
