using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Guarda de reentrega: o reenvio não pode ressuscitar um convite de chat de uma
/// sessão que já morreu — era exatamente o sintoma "a janela do chat abriu do
/// nada" (convite reenviado ~6 min depois, com a sessão já encerrada).
/// </summary>
[TestFixture]
public class CommandRedeliveryGuardTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 2, 38, 41, DateTimeKind.Utc);

    private static TicketChatSession Session(TicketChatSessionStatus status, DateTime? expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        TicketId = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        Status = status,
        StartedAt = Now.AddMinutes(-6),
        InviteExpiresAt = expiresAt,
        CreatedAt = Now.AddMinutes(-6),
        UpdatedAt = Now.AddMinutes(-6),
    };

    [Test]
    public void PendingSessionWithinTheInviteWindowIsAllowed()
    {
        var decision = CommandRedeliveryGuard.EvaluateSession(
            Session(TicketChatSessionStatus.Pending, Now.AddMinutes(1)), Now);

        Assert.That(decision.Allowed, Is.True, decision.Reason);
    }

    [Test]
    public void PendingSessionWithoutExpiryIsAllowed()
    {
        var decision = CommandRedeliveryGuard.EvaluateSession(
            Session(TicketChatSessionStatus.Pending, null), Now);

        Assert.That(decision.Allowed, Is.True, decision.Reason);
    }

    [Test]
    public void EndedSessionIsBlocked()
    {
        // Caso real (12/10/2026 02:38:41 UTC): o convite da sessão dd00ef08 chegou
        // 5m54s depois de criado, 4 min após o usuário encerrar a conversa.
        var decision = CommandRedeliveryGuard.EvaluateSession(
            Session(TicketChatSessionStatus.Ended, Now.AddMinutes(-4)), Now);

        Assert.That(decision.Allowed, Is.False);
        Assert.That(decision.Reason, Does.Contain("Ended"));
    }

    [TestCase(TicketChatSessionStatus.Active)]
    [TestCase(TicketChatSessionStatus.Declined)]
    [TestCase(TicketChatSessionStatus.Expired)]
    [TestCase(TicketChatSessionStatus.Ended)]
    public void NonPendingSessionsAreBlocked(TicketChatSessionStatus status)
    {
        var decision = CommandRedeliveryGuard.EvaluateSession(Session(status, Now.AddMinutes(1)), Now);

        Assert.That(decision.Allowed, Is.False);
    }

    [Test]
    public void ExpiredInviteIsBlockedEvenWhilePending()
    {
        var decision = CommandRedeliveryGuard.EvaluateSession(
            Session(TicketChatSessionStatus.Pending, Now.AddSeconds(-1)), Now);

        Assert.That(decision.Allowed, Is.False);
        Assert.That(decision.Reason, Is.EqualTo("convite de chat expirado"));
    }

    [Test]
    public void UnknownSessionIsBlocked()
    {
        var decision = CommandRedeliveryGuard.EvaluateSession(null, Now);

        Assert.That(decision.Allowed, Is.False);
        Assert.That(decision.Reason, Is.EqualTo("sessão de chat não encontrada"));
    }

    [Test]
    public void SessionIdIsReadFromTheCommandPayload()
    {
        var sessionId = Guid.NewGuid();
        var payload = $"{{\"sessionId\":\"{sessionId}\",\"ticketId\":\"{Guid.NewGuid()}\",\"expiresAtUtc\":\"2026-10-12T02:34:46.6479901Z\"}}";

        Assert.That(CommandRedeliveryGuard.TryReadSessionId(payload, out var parsed), Is.True);
        Assert.That(parsed, Is.EqualTo(sessionId));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("[]")]
    [TestCase("{\"ticketId\":\"01a12af7-c958-7c37-a15a-f78493022277\"}")]
    [TestCase("{\"sessionId\":\"nao-e-guid\"}")]
    [TestCase("{\"sessionId\":123}")]
    [TestCase("nao-e-json")]
    public void InvalidPayloadsAreRejected(string payload)
    {
        Assert.That(CommandRedeliveryGuard.TryReadSessionId(payload, out _), Is.False);
    }
}

/// <summary>
/// Comando de energia (restart/shutdown) não pode entrar em reentrega repetida:
/// o reboot derruba o agente antes do result/ack, então cada reenvio é um novo
/// reboot. Caso real: 1 restart do console virou 54 reboots em ~5h (~6 em 6 min),
/// porque a reentrega reenviava a cada RetryGraceSeconds=300 até a retenção de 24h.
/// A deduplicação por CommandId do agente não salva aqui: o estado dela morre no
/// reboot.
/// </summary>
[TestFixture]
public class CommandPowerRedeliveryGuardTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 2, 38, 41, DateTimeKind.Utc);

    private static AgentCommand Command(
        CommandType commandType = CommandType.Restart,
        CommandStatus status = CommandStatus.Sent,
        DateTime? sentAt = null) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        CommandType = commandType,
        Payload = "{\"delaySeconds\":0,\"force\":true}",
        Status = status,
        CreatedAt = Now.AddMinutes(-6),
        SentAt = sentAt ?? (status == CommandStatus.Pending ? null : Now.AddMinutes(-6)),
    };

    [TestCase(CommandType.Restart)]
    [TestCase(CommandType.Shutdown)]
    public void PublishedPowerCommandIsNotRedelivered(CommandType commandType)
    {
        var decision = CommandRedeliveryGuard.EvaluatePowerRedelivery(Command(commandType));

        Assert.That(decision.Allowed, Is.False, "comando de energia já publicado não pode ser reenviado");
    }

    [Test]
    public void RunningPowerCommandIsNotRedelivered()
    {
        var decision = CommandRedeliveryGuard.EvaluatePowerRedelivery(
            Command(status: CommandStatus.Running));

        Assert.That(decision.Allowed, Is.False);
    }

    [Test]
    public void SentPowerCommandWithoutSentAtIsNotRedelivered()
    {
        // Defensivo: mesmo sem SentAt, Status=Sent prova a publicação.
        var decision = CommandRedeliveryGuard.EvaluatePowerRedelivery(
            Command(status: CommandStatus.Sent, sentAt: null));

        Assert.That(decision.Allowed, Is.False);
    }

    [Test]
    public void IgnoredPowerCommandIsNotMarkedTerminal()
    {
        var decision = CommandRedeliveryGuard.EvaluatePowerRedelivery(Command());

        Assert.That(decision.Allowed, Is.False);
        Assert.That(decision.MarkTerminal, Is.False,
            "o reboot provavelmente aconteceu: encerrar como Cancelled seria enganoso");
        Assert.That(decision.Reason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void NeverPublishedPowerCommandKeepsBeingRedelivered()
    {
        // Agente/transporte fora do ar: o dispatch inicial não publicou (fica
        // Pending, sem SentAt) e o restart precisa chegar quando o agente voltar.
        var decision = CommandRedeliveryGuard.EvaluatePowerRedelivery(
            Command(status: CommandStatus.Pending, sentAt: null));

        Assert.That(decision.Allowed, Is.True, "primeira entrega de energia não pode ser bloqueada");
    }

    [TestCase(CommandType.PowerShell)]
    [TestCase(CommandType.Script)]
    [TestCase(CommandType.TicketChatInvite)]
    [TestCase(CommandType.Update)]
    public void NonPowerCommandsAreNotAffectedByThePowerRule(CommandType commandType)
    {
        Assert.That(CommandRedeliveryGuard.IsPowerCommand(commandType), Is.False);
    }

    [TestCase(CommandType.Restart)]
    [TestCase(CommandType.Shutdown)]
    public void PowerCommandsAreFlagged(CommandType commandType)
    {
        Assert.That(CommandRedeliveryGuard.IsPowerCommand(commandType), Is.True);
    }

    [Test]
    public void HistoricalDecisionsKeepCancellingTheCommand()
    {
        // Retrocompatibilidade: nada muda para quem usa Block/Allow ou o
        // construtor posicional (o default de MarkTerminal é true).
        Assert.That(CommandRedeliveryDecision.Block("motivo").MarkTerminal, Is.True);
        Assert.That(new CommandRedeliveryDecision(false, "motivo").MarkTerminal, Is.True);
        Assert.That(CommandRedeliveryDecision.Allow.MarkTerminal, Is.True);
        Assert.That(CommandRedeliveryDecision.Ignore("motivo").MarkTerminal, Is.False);
    }
}
