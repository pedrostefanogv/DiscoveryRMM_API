using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Services;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Regressão do incidente de 54 reboots: comando de patch que termina em
/// reinício/desligamento não pode ser reentregue depois de publicado.
/// </summary>
[TestFixture]
public class PatchCommandRedeliveryGuardTests
{
    private static AgentCommand PatchCommand(string payload) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        CommandType = CommandType.WindowsUpdate,
        Payload = payload,
        CreatedAt = DateTime.UtcNow
    };

    [TestCase("""{ "action": "install", "postAction": "restart" }""", true)]
    [TestCase("""{ "action": "install", "postAction": "shutdown" }""", true)]
    [TestCase("""{ "action": "install", "postAction": "none" }""", false)]
    [TestCase("""{ "action": "scan" }""", false)]
    [TestCase("nao-json", false)]
    public void DetectsPatchWithPowerAction(string payload, bool expected)
    {
        Assert.That(CommandRedeliveryGuard.IsPatchWithPowerAction(PatchCommand(payload)), Is.EqualTo(expected));
    }

    [Test]
    public void PublishedPatchWithPowerActionIsNotRedelivered()
    {
        var command = PatchCommand("""{ "action": "install", "postAction": "restart" }""");
        command.Status = CommandStatus.Sent;
        command.SentAt = DateTime.UtcNow;

        var decision = CommandRedeliveryGuard.EvaluatePowerRedelivery(command);
        Assert.That(decision.Allowed, Is.False);
    }

    [Test]
    public void UnpublishedPatchWithPowerActionRemainsEligible()
    {
        var command = PatchCommand("""{ "action": "install", "postAction": "restart" }""");
        command.Status = CommandStatus.Pending;

        var decision = CommandRedeliveryGuard.EvaluatePowerRedelivery(command);
        Assert.That(decision.Allowed, Is.True);
    }

    [Test]
    public void PlainPatchCommandIsNotTreatedAsPowerCommand()
    {
        var command = PatchCommand("""{ "action": "scan" }""");
        Assert.That(CommandRedeliveryGuard.IsPatchWithPowerAction(command), Is.False);
    }
}
