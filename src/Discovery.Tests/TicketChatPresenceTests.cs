using Discovery.Core.Configuration;
using Discovery.Core.Cqrs.Tickets.Chat.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Tickets.Chat;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Notificação de nova mensagem no chat do chamado.
///
/// REGRA: se o atendente que abriu a conversa está com a JANELA DO CHAT aberta e
/// visível, ele já está lendo a mensagem — não faz sentido criar notificação
/// (sino + Web Push). Antes, TODA mensagem do usuário final virava notificação,
/// mesmo com a conversa aberta na frente do atendente, o que enchia a caixa e
/// disparava aviso do navegador para algo já lido.
/// </summary>
public class TicketChatPresenceTests
{
    private static readonly Guid SessionId = Guid.Parse("6a7f0f9c-2b7c-4d1a-9f0e-3a6a1b2c3d4e");
    private static readonly Guid TicketId = Guid.Parse("01a12af7-c958-7c37-a15a-f78493022277");
    private static readonly Guid AttendantUserId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static TicketChatSession ActiveSession() => new()
    {
        Id = SessionId,
        TicketId = TicketId,
        AgentId = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        SiteId = Guid.NewGuid(),
        Status = TicketChatSessionStatus.Active,
        StartedByUserId = AttendantUserId,
        StartedByName = "Ana",
        StartedAt = DateTime.UtcNow.AddMinutes(-5),
        AcceptedAt = DateTime.UtcNow.AddMinutes(-4)
    };

    private static SendTicketChatMessageCommand EndUserMessage() => new(
        SessionId,
        AuthorUserId: null,
        AuthorName: "Cliente",
        AuthorKind: "enduser",
        Body: "bom dia, preciso de ajuda",
        AttachmentIds: null,
        ClientMessageId: null);

    // ── Serviço de presença ───────────────────────────────────────────────

    [Test]
    public async Task Presence_IsTrackedPerUserAndTicket()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var presence = new TicketChatPresenceService(clock);

        await presence.TouchAsync(AttendantUserId, TicketId);

        var sameUserSameTicket = await presence.IsViewingAsync(AttendantUserId, TicketId);
        var otherUser = await presence.IsViewingAsync(Guid.NewGuid(), TicketId);
        var otherTicket = await presence.IsViewingAsync(AttendantUserId, Guid.NewGuid());

        Assert.Multiple(() =>
        {
            Assert.That(sameUserSameTicket, Is.True);
            Assert.That(otherUser, Is.False, "outro atendente não herda a presença");
            Assert.That(otherTicket, Is.False, "outro chamado não herda a presença");
        });
    }

    [Test]
    public async Task Presence_Expires_WhenHeartbeatStops()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var presence = new TicketChatPresenceService(clock);

        await presence.TouchAsync(AttendantUserId, TicketId);
        clock.Advance(TimeSpan.FromSeconds(presence.TtlSeconds + 1));

        Assert.That(await presence.IsViewingAsync(AttendantUserId, TicketId), Is.False,
            "se o console morrer sem avisar, a notificação volta sozinha depois do TTL");
    }

    [Test]
    public async Task Presence_Clear_RemovesImmediately()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var presence = new TicketChatPresenceService(clock);

        await presence.TouchAsync(AttendantUserId, TicketId);
        await presence.ClearAsync(AttendantUserId, TicketId);

        Assert.That(await presence.IsViewingAsync(AttendantUserId, TicketId), Is.False,
            "fechar a janela do chat precisa liberar a notificação na hora");
    }

    [Test]
    public async Task Presence_StaleClear_DoesNotDropNewerWindow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var presence = new TicketChatPresenceService(clock);

        // React StrictMode monta/desmonta/monta: o DELETE da primeira montagem
        // pode chegar DEPOIS do POST da segunda. Sem o dono da presenca, a
        // supressao falharia por corrida com a janela do chat aberta.
        await presence.TouchAsync(AttendantUserId, TicketId, "instancia-1");
        await presence.TouchAsync(AttendantUserId, TicketId, "instancia-2");
        await presence.ClearAsync(AttendantUserId, TicketId, "instancia-1");

        Assert.That(await presence.IsViewingAsync(AttendantUserId, TicketId), Is.True,
            "o clear atrasado de uma montagem antiga não pode derrubar a presença da janela atual");

        await presence.ClearAsync(AttendantUserId, TicketId, "instancia-2");

        Assert.That(await presence.IsViewingAsync(AttendantUserId, TicketId), Is.False,
            "o dono atual continua podendo limpar a presença");
    }

    // ── Supressão no envio da mensagem do usuário final ───────────────────

    [Test]
    public async Task EndUserMessage_DoesNotNotify_WhenAttendantIsViewingChat()
    {
        var notifications = new RecordingNotificationService();
        var (chatHandler, publisher) = BuildHandler(notifications, new StubPresenceService(isViewing: true));

        var result = await chatHandler.Handle(EndUserMessage(), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(notifications.Published, Is.Empty,
                "atendente com a janela do chat aberta não deve receber notificação de mensagem que já está lendo");
            Assert.That(publisher.Published, Does.Contain("ActivitySignal"),
                "o sinal de tempo real continua obrigatório para a conversa atualizar");
        });
    }

    [Test]
    public async Task EndUserMessage_Notifies_WhenAttendantIsNotViewingChat()
    {
        var notifications = new RecordingNotificationService();
        var (chatHandler, _) = BuildHandler(notifications, new StubPresenceService(isViewing: false));

        var result = await chatHandler.Handle(EndUserMessage(), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(notifications.Published, Has.Count.EqualTo(1),
            "janela do chat fechada/oculta continua gerando o aviso normal");
        Assert.Multiple(() =>
        {
            Assert.That(notifications.Published[0].RecipientUserId, Is.EqualTo(AttendantUserId));
            Assert.That(notifications.Published[0].Topic, Is.EqualTo("ticket-chat"));
            Assert.That(notifications.Published[0].Payload, Is.Not.Null);
        });
    }

    private static (SendTicketChatMessageCommandHandler Handler, RecordingEnvelopePublisher Publisher) BuildHandler(
        RecordingNotificationService notifications,
        ITicketChatPresenceService presence)
    {
        var repository = new FakeTicketChatRepository { Session = ActiveSession() };
        var publisher = new RecordingEnvelopePublisher();

        var handler = new SendTicketChatMessageCommandHandler(
            repository,
            publisher,
            notifications,
            presence,
            Options.Create(new TicketChatOptions()),
            NullLogger<SendTicketChatMessageCommandHandler>.Instance);

        return (handler, publisher);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class StubPresenceService(bool isViewing) : ITicketChatPresenceService
    {
        public int TtlSeconds => 75;
        public Task TouchAsync(Guid userId, Guid ticketId, string? clientId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(Guid userId, Guid ticketId, string? clientId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsViewingAsync(Guid userId, Guid ticketId, CancellationToken cancellationToken = default)
            => Task.FromResult(isViewing);
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<NotificationPublishRequest> Published { get; } = [];

        public Task<AppNotification> PublishAsync(NotificationPublishRequest request, CancellationToken cancellationToken = default)
        {
            Published.Add(request);
            return Task.FromResult(new AppNotification { Id = Guid.NewGuid(), RecipientUserId = request.RecipientUserId });
        }

        public Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50)
            => Task.FromResult<IReadOnlyList<AppNotification>>([]);

        public Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null)
            => Task.FromResult(false);

        public Task<bool> DeleteAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null)
            => Task.FromResult(false);
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

    private sealed class FakeTicketChatRepository : ITicketChatRepository
    {
        public TicketChatSession? Session { get; set; }

        public Task<TicketChatSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Session is not null && Session.Id == id ? Session : null);

        public Task<(TicketChatMessage Message, bool Created)> AppendMessageAsync(
            Guid session, TicketChatMessage message, IReadOnlyList<Guid> attachmentIds, CancellationToken cancellationToken = default)
        {
            message.SequenceNumber = 1;
            return Task.FromResult((message, true));
        }

        public Task<IReadOnlyList<TicketChatAttachmentRef>> GetAttachmentsForMessagesAsync(
            IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TicketChatAttachmentRef>>([]);

        public Task UpdateSessionAsync(TicketChatSession updated, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TicketChatSession?> GetCurrentForTicketAsync(Guid ticketId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TicketChatSession?> GetCurrentForAgentAsync(Guid agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddSessionAsync(TicketChatSession newSession, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TicketChatSession>> GetHistoryAsync(Guid ticketId, int skip, int take, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TicketChatMessage>> GetMessagesAsync(Guid session, long afterSequence, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TicketChatSession>> GetExpiredPendingAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TicketChatSession>> GetInactiveActiveAsync(DateTime cutoffUtc, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TicketChatSession>> GetActiveForClosedTicketsAsync(int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
