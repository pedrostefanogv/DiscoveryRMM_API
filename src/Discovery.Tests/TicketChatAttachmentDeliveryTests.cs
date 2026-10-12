using Discovery.Core.Configuration;
using Discovery.Core.Cqrs.Tickets.Chat.Commands;
using Discovery.Core.DTOs;
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
/// Entrega dos ANEXOS do chat de suporte no sentido servidor -> agent.
///
/// BUG CORRIGIDO: TicketChatEnvelopePublisher.PublishMessageToAgentAsync montava
/// o envelope sem AttachmentIds (o campo existia no contrato e nunca era
/// preenchido). O print enviado pelo suporte chegava ao usuário final sem o
/// anexo. Estes testes travam o contrato.
/// </summary>
public class TicketChatAttachmentDeliveryTests
{
    private static readonly Guid SessionId = Guid.Parse("6a7f0f9c-2b7c-4d1a-9f0e-3a6a1b2c3d4e");
    private static readonly Guid TicketId = Guid.Parse("01a12af7-c958-7c37-a15a-f78493022277");
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

    private static TicketChatMessage SupportMessage(string body) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = SessionId,
        ClientMessageId = Guid.NewGuid(),
        AuthorKind = TicketChatAuthorKind.Support,
        AuthorName = "Ana",
        Body = body,
        CreatedAt = DateTime.UtcNow,
        SequenceNumber = 3
    };

    [Test]
    public async Task PublishMessageToAgent_IncludesAttachmentIds()
    {
        var attachmentId = Guid.NewGuid();
        var repository = new FakeTicketChatRepository
        {
            AttachmentsToReturn = [new TicketChatAttachmentRef(Guid.Empty, attachmentId, "print.png", "image/png", 2048, "suporte", DateTime.UtcNow)]
        };
        var messaging = new RecordingAgentMessaging();
        var publisher = new TicketChatEnvelopePublisher(
            messaging, null!, null!, repository, NullLogger<TicketChatEnvelopePublisher>.Instance);

        await publisher.PublishMessageToAgentAsync(ActiveSession(), SupportMessage("segue o print"));

        Assert.That(messaging.LastEnvelope, Is.Not.Null, "o envelope da mensagem deveria ter sido publicado");
        Assert.Multiple(() =>
        {
            Assert.That(messaging.LastEnvelope!.EventType, Is.EqualTo(TicketChatEventTypes.Message));
            Assert.That(messaging.LastEnvelope.Body, Is.EqualTo("segue o print"));
            Assert.That(messaging.LastEnvelope.AttachmentIds, Is.EqualTo(new[] { attachmentId }),
                "sem os ids dos anexos o agent não tem como exibir/baixar a imagem");
        });
    }

    [Test]
    public async Task PublishMessageToAgent_WithoutAttachments_KeepsIdsNull()
    {
        var repository = new FakeTicketChatRepository { AttachmentsToReturn = [] };
        var messaging = new RecordingAgentMessaging();
        var publisher = new TicketChatEnvelopePublisher(
            messaging, null!, null!, repository, NullLogger<TicketChatEnvelopePublisher>.Instance);

        await publisher.PublishMessageToAgentAsync(ActiveSession(), SupportMessage("só texto"));

        Assert.That(messaging.LastEnvelope, Is.Not.Null);
        Assert.That(messaging.LastEnvelope!.AttachmentIds, Is.Null);
    }

    [Test]
    public async Task SendMessage_AcceptsAttachmentOnly()
    {
        var session = ActiveSession();
        var attachmentId = Guid.NewGuid();
        var repository = new FakeTicketChatRepository { Session = session };
        var publisher = new RecordingEnvelopePublisher();

        var handler = new SendTicketChatMessageCommandHandler(
            repository,
            publisher,
            new NoopNotificationService(),
            new NotViewingPresenceService(),
            Options.Create(new TicketChatOptions()),
            NullLogger<SendTicketChatMessageCommandHandler>.Instance);

        var result = await handler.Handle(
            new SendTicketChatMessageCommand(
                SessionId,
                AuthorUserId: null,
                AuthorName: "Ana",
                AuthorKind: "support",
                Body: string.Empty,
                AttachmentIds: [attachmentId],
                ClientMessageId: null),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True,
            "mensagem SÓ com anexo (print sem legenda) precisa ser aceita: " +
            string.Join(";", result.Errors.Select(e => e.Code)));
        Assert.Multiple(() =>
        {
            Assert.That(repository.LastAttachmentIds, Is.EqualTo(new[] { attachmentId }),
                "o vínculo do anexo precisa ser persistido com a mensagem");
            Assert.That(result.Value!.Attachments, Has.Count.EqualTo(1));
            Assert.That(publisher.Published, Does.Contain("Message"),
                "a mensagem do suporte precisa seguir para o agent");
        });
    }

    // ── Fakes ─────────────────────────────────────────────────────────────

    private sealed class RecordingAgentMessaging : IAgentMessaging
    {
        public TicketChatEnvelope? LastEnvelope { get; private set; }

        public bool IsConnected => true;

        public Task PublishSupportChatServerAsync(Guid clientId, Guid siteId, Guid agentId, TicketChatEnvelope envelope, CancellationToken cancellationToken = default)
        {
            LastEnvelope = envelope;
            return Task.CompletedTask;
        }

        public Task SendCommandAsync(Guid agentId, Guid commandId, string commandType, string payload) => throw new NotSupportedException();
        public Task PublishSiteFanoutCommandAsync(Guid clientId, Guid siteId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PublishClientFanoutCommandAsync(Guid clientId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PublishGlobalFanoutCommandAsync(CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PublishDashboardEventAsync(DashboardEventMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, Guid overrideClientId, Guid overrideSiteId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendCommandToSubjectAsync(Guid clientId, Guid siteId, Guid agentId, Guid commandId, string commandType, string payload) => throw new NotSupportedException();
        public Task PublishRemoteDebugControlAsync(Guid clientId, Guid siteId, Guid agentId, string payload, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SubscribeToAgentMessagesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
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

        public IReadOnlyList<TicketChatAttachmentRef> AttachmentsToReturn { get; set; } = [];

        public IReadOnlyList<Guid> LastAttachmentIds { get; private set; } = [];

        public Task<TicketChatSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Session is not null && Session.Id == id ? Session : null);

        public Task<(TicketChatMessage Message, bool Created)> AppendMessageAsync(
            Guid session, TicketChatMessage message, IReadOnlyList<Guid> attachmentIds, CancellationToken cancellationToken = default)
        {
            LastAttachmentIds = attachmentIds;
            message.SequenceNumber = 1;
            // O handler consulta os anexos da mensagem salva pelo id gerado nele.
            AttachmentsToReturn = attachmentIds
                .Select(id => new TicketChatAttachmentRef(message.Id, id, "print.png", "image/png", 2048, "suporte", DateTime.UtcNow))
                .ToList();
            return Task.FromResult((message, true));
        }

        public Task<IReadOnlyList<TicketChatAttachmentRef>> GetAttachmentsForMessagesAsync(
            IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default)
            => Task.FromResult(AttachmentsToReturn);

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

    /// <summary>Atendente não está vendo o chat: o caminho de notificação segue normal.</summary>
    private sealed class NotViewingPresenceService : ITicketChatPresenceService
    {
        public int TtlSeconds => 75;
        public Task TouchAsync(Guid userId, Guid ticketId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(Guid userId, Guid ticketId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsViewingAsync(Guid userId, Guid ticketId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class NoopNotificationService : INotificationService
    {
        public Task<AppNotification> PublishAsync(NotificationPublishRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new AppNotification());

        public Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50)
            => Task.FromResult<IReadOnlyList<AppNotification>>([]);

        public Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null)
            => Task.FromResult(false);

        public Task<bool> DeleteAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null)
            => Task.FromResult(false);
    }
}