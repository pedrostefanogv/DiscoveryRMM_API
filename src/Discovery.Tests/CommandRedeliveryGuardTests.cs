using Discovery.Core.Entities;
using Discovery.Core.Enums;
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
