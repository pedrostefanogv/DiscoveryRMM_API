using Discovery.Core.Cqrs.Tickets.Chat.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Tickets.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Contrato de SINALIZAÇÃO do encerramento do chat.
///
/// BUG CORRIGIDO: `EndTicketChatSessionCommandHandler` publicava apenas
/// `SessionEnded` (canal servidor -> agent). O console não faz polling, então o
/// operador de suporte NÃO via que o usuário do PC encerrou a conversa. Este
/// teste trava o contrato: encerrar precisa publicar TAMBÉM o sinal de estado
/// que o console consome.
/// </summary>
public class TicketChatEndSignalTests
{
    private static readonly Guid TicketId = Guid.Parse("01a12af7-c958-7c37-a15a-f78493022277");
    private static readonly Guid SessionId = Guid.Parse("6a7f0f9c-2b7c-4d1a-9f0e-3a6a1b2c3d4e");
    private static readonly Guid AgentId = Guid.Parse("019faa6f-8eaa-7c38-9878-36cc96f9be3e");

    private static TicketChatSession ActiveSession() => new()
    {
        Id = SessionId,
        TicketId = TicketId,
        AgentId = AgentId,
        ClientId = Guid.NewGuid(),
        SiteId = Guid.NewGuid(),
        Status = TicketChatSessionStatus.Active,
        StartedAt = DateTime.UtcNow.AddMinutes(-5),
        AcceptedAt = DateTime.UtcNow.AddMinutes(-4)
    };

    [Test]
    public async Task End_ShouldPublishSessionEnded_AndStatusSignalForConsole()
    {
        var session = ActiveSession();
        var publisher = new RecordingEnvelopePublisher();

        var repository = new FakeTicketChatRepository(session);
        var handler = new EndTicketChatSessionCommandHandler(
            repository,
            publisher,
            new NoopActivityLogService(),
            NullLogger<EndTicketChatSessionCommandHandler>.Instance);

        var result = await handler.Handle(
            new EndTicketChatSessionCommand(SessionId, "user", "user_closed"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True, "encerrar deveria ter sucesso");
        Assert.That(session.Status, Is.EqualTo(TicketChatSessionStatus.Ended));
        Assert.That(session.EndedBy, Is.EqualTo(TicketChatEndedBy.User));

        Assert.Multiple(() =>
        {
            Assert.That(
                publisher.Published,
                Does.Contain("SessionEnded"),
                "o agent precisa receber SessionEnded para fechar a janela");
            Assert.That(
                publisher.Published,
                Does.Contain("StatusSignal"),
                "o CONSOLE precisa receber o sinal de estado — sem ele o operador não vê que o usuário encerrou");

            // Quem encerrou fica no HISTÓRICO (mensagem de sistema), não só no
            // log de atividades do chamado: os dois lados leem isso na thread.
            Assert.That(repository.Appended, Has.Count.EqualTo(1), "encerrar deve gravar a mensagem de sistema");
            Assert.That(repository.Appended[0].AuthorKind, Is.EqualTo(TicketChatAuthorKind.System));
            Assert.That(repository.Appended[0].Body, Does.Contain("usuário"));
            Assert.That(publisher.Published, Does.Contain("Message"), "a mensagem de sistema precisa ir para o agent");

            // ORDEM: a nota precisa chegar ANTES do SessionEnded — depois dele a
            // janela do agent entra em leitura e a mensagem não seria aplicada.
            var messageIndex = publisher.Published.IndexOf("Message");
            var endedIndex = publisher.Published.IndexOf("SessionEnded");
            Assert.That(messageIndex, Is.LessThan(endedIndex), "a mensagem de sistema deve preceder o SessionEnded");
        });
    }

    private sealed class RecordingEnvelopePublisher : ITicketChatEnvelopePublisher
    {
        public List<string> Published { get; } = [];

        public Task PublishSessionOpenedAsync(TicketChatSession session, CancellationToken cancellationToken = default)
        {
            Published.Add("SessionOpened");
            return Task.CompletedTask;
        }

        public Task PublishMessageToAgentAsync(TicketChatSession session, TicketChatMessage message, CancellationToken cancellationToken = default)
        {
            Published.Add("Message");
            return Task.CompletedTask;
        }

        public Task PublishSessionEndedAsync(TicketChatSession session, CancellationToken cancellationToken = default)
        {
            Published.Add("SessionEnded");
            return Task.CompletedTask;
        }

        public Task PublishActivitySignalAsync(TicketChatSession session, TicketChatMessage message, CancellationToken cancellationToken = default)
        {
            Published.Add("ActivitySignal");
            return Task.CompletedTask;
        }

        public Task PublishSessionStatusSignalAsync(TicketChatSession session, CancellationToken cancellationToken = default)
        {
            Published.Add("StatusSignal");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTicketChatRepository(TicketChatSession session) : ITicketChatRepository
    {
        public List<TicketChatMessage> Appended { get; } = [];

        public Task<TicketChatSession?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<TicketChatSession?>(session.Id == sessionId ? session : null);

        public Task UpdateSessionAsync(TicketChatSession updated, CancellationToken cancellationToken = default)
        {
            session.Status = updated.Status;
            session.EndedAt = updated.EndedAt;
            session.EndedBy = updated.EndedBy;
            session.EndReason = updated.EndReason;
            return Task.CompletedTask;
        }

        public Task<TicketChatSession?> GetCurrentForTicketAsync(Guid ticketId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TicketChatSession?> GetCurrentForAgentAsync(Guid agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddSessionAsync(TicketChatSession newSession, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TicketChatSession>> GetHistoryAsync(Guid ticketId, int skip, int take, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TicketChatMessage>> GetMessagesAsync(Guid sessionId, long afterSequence, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(TicketChatMessage Message, bool Created)> AppendMessageAsync(Guid sessionId, TicketChatMessage message, IReadOnlyList<Guid> attachmentIds, CancellationToken cancellationToken = default)
        {
            message.SequenceNumber = Appended.Count + 1;
            Appended.Add(message);
            return Task.FromResult((message, true));
        }
        public Task<IReadOnlyList<TicketChatAttachmentRef>> GetAttachmentsForMessagesAsync(IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TicketChatSession>> GetExpiredPendingAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TicketChatSession>> GetInactiveActiveAsync(DateTime cutoffUtc, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TicketChatSession>> GetActiveForClosedTicketsAsync(int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopActivityLogService : IActivityLogService
    {
        public Task<TicketActivityLog> LogStateChangeAsync(Guid ticketId, Guid? changedByUserId, Guid oldStateId, Guid newStateId) => throw new NotSupportedException();
        public Task<TicketActivityLog> LogAssignmentAsync(Guid ticketId, Guid? changedByUserId, Guid? oldUserId, Guid? newUserId) => throw new NotSupportedException();
        public Task<TicketActivityLog> LogActivityAsync(Guid ticketId, TicketActivityType type, Guid? changedByUserId, string? oldValue = null, string? newValue = null, string? comment = null)
            => Task.FromResult(new TicketActivityLog { Id = Guid.NewGuid(), TicketId = ticketId, Type = type, Comment = comment, CreatedAt = DateTime.UtcNow });
        public Task<TicketActivityLog> LogPriorityChangeAsync(Guid ticketId, Guid? changedByUserId, string oldPriority, string newPriority) => throw new NotSupportedException();
        public Task<TicketActivityLog> LogDepartmentChangeAsync(Guid ticketId, Guid? changedByUserId, string oldDept, string newDept) => throw new NotSupportedException();
    }
}
