using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Guarda de reentrega vista pelo comando (não só pela sessão): é este o ponto em
/// que o laço genérico de reentrega consulta o domínio do chat. Um convite
/// EXPirado nunca pode ser reenviado ao agent — era assim que a janela do chat
/// abria "do nada" depois de a sessão já ter morrido.
/// </summary>
[TestFixture]
public class TicketChatInviteRedeliveryGuardTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 2, 38, 41, DateTimeKind.Utc);

    private static AgentCommand InviteCommand(Guid sessionId) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        CommandType = CommandType.TicketChatInvite,
        Payload = $"{{\"sessionId\":\"{sessionId}\",\"expiresAtUtc\":\"{Now.AddSeconds(-1):O}\"}}",
        Status = CommandStatus.Pending,
        CreatedAt = Now.AddMinutes(-6),
    };

    private static TicketChatSession Session(TicketChatSessionStatus status, DateTime? expiresAt, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        Status = status,
        StartedAt = Now.AddMinutes(-6),
        InviteExpiresAt = expiresAt,
        CreatedAt = Now.AddMinutes(-6),
        UpdatedAt = Now.AddMinutes(-6),
    };

    private sealed class StubRepository(TicketChatSession? session) : ITicketChatRepository
    {
        public Task<TicketChatSession?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(session);

        public Task<TicketChatSession?> GetCurrentForTicketAsync(Guid ticketId, CancellationToken cancellationToken = default) => Task.FromResult<TicketChatSession?>(null);
        public Task<TicketChatSession?> GetCurrentForAgentAsync(Guid agentId, CancellationToken cancellationToken = default) => Task.FromResult<TicketChatSession?>(null);
        public Task<TicketChatSession?> GetLatestForTicketAsync(Guid ticketId, CancellationToken cancellationToken = default) => Task.FromResult(session);
        public Task<TicketChatSession?> GetLatestForAgentAsync(Guid agentId, CancellationToken cancellationToken = default) => Task.FromResult(session);
        public Task AddSessionAsync(TicketChatSession s, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateSessionAsync(TicketChatSession s, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TicketChatSession>> GetHistoryAsync(Guid ticketId, int skip, int take, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TicketChatSession>>([]);
        public Task<IReadOnlyList<TicketChatMessage>> GetMessagesAsync(Guid sessionId, long afterSequence, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TicketChatMessage>>([]);
        public Task<(TicketChatMessage Message, bool Created)> AppendMessageAsync(Guid sessionId, TicketChatMessage message, IReadOnlyList<Guid> attachmentIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TicketChatAttachmentRef>> GetAttachmentsForMessagesAsync(IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TicketChatAttachmentRef>>([]);
        public Task<IReadOnlyList<TicketChatSession>> GetExpiredPendingAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TicketChatSession>>([]);
        public Task<IReadOnlyList<TicketChatSession>> GetInactiveActiveAsync(DateTime cutoffUtc, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TicketChatSession>>([]);
        public Task<IReadOnlyList<TicketChatSession>> GetActiveForClosedTicketsAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TicketChatSession>>([]);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    [Test]
    public async Task ExpiredInviteIsNotRedelivered()
    {
        var session = Session(TicketChatSessionStatus.Pending, Now.AddSeconds(-1));
        var guard = new CommandRedeliveryGuard(new StubRepository(session), new FixedTimeProvider(Now));

        var decision = await guard.EvaluateAsync(InviteCommand(session.Id));

        Assert.That(decision.Allowed, Is.False, "convite vencido não pode voltar ao agent");
        Assert.That(decision.MarkTerminal, Is.True, "convite vencido é terminal: encerra como Cancelled");
    }

    [Test]
    public async Task PendingInviteWithinTheWindowIsRedelivered()
    {
        var session = Session(TicketChatSessionStatus.Pending, Now.AddMinutes(1));
        var guard = new CommandRedeliveryGuard(new StubRepository(session), new FixedTimeProvider(Now));

        var decision = await guard.EvaluateAsync(InviteCommand(session.Id));

        Assert.That(decision.Allowed, Is.True, "convite ainda válido pode ser reentregue");
    }

    [Test]
    public async Task DeclinedSessionIsNotRedelivered()
    {
        var session = Session(TicketChatSessionStatus.Declined, Now.AddMinutes(1));
        var guard = new CommandRedeliveryGuard(new StubRepository(session), new FixedTimeProvider(Now));

        var decision = await guard.EvaluateAsync(InviteCommand(session.Id));

        Assert.That(decision.Allowed, Is.False);
    }

    [Test]
    public void ExpiredInviteDecisionCancelsTheCommand()
    {
        // Block (MarkTerminal=true) -> o laço encerra o comando como Cancelled,
        // que é a verdade do histórico (o convite não deve mais ser entregue).
        var decision = CommandRedeliveryGuard.EvaluateTicketChatInvite(
            Session(TicketChatSessionStatus.Pending, Now.AddSeconds(-1)), Now);

        Assert.That(discoveryAction(decision), Is.EqualTo("Cancel"));
    }

    private static string discoveryAction(CommandRedeliveryDecision decision)
        => decision.Allowed ? "Send" : decision.MarkTerminal ? "Cancel" : "Ignore";
}
