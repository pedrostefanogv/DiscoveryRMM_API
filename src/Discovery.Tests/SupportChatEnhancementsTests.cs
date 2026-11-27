using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Cqrs.Tickets.Chat.Commands;
using Discovery.Core.Cqrs.Tickets.Dtos;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Tickets.Chat;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Discovery.Tests;

public class SupportChatEnhancementsTests
{
    private static TicketChatSession CreateActiveSession(Guid? ticketId = null) => new()
    {
        Id = Guid.NewGuid(),
        TicketId = ticketId,
        AgentId = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        SiteId = Guid.NewGuid(),
        Status = TicketChatSessionStatus.Active,
        StartedByUserId = Guid.NewGuid(),
        StartedByName = "Técnico Origem",
        StartedAt = DateTime.UtcNow.AddMinutes(-2),
    };

    [Test]
    public async Task AssistedAction_RequestScreenshot_PersistsSystemMessageAndPublishes()
    {
        var session = CreateActiveSession(Guid.NewGuid());
        var repo = new StubChatRepo(session);
        var publisher = new RecordingPublisher();
        var resolver = new FixedNameResolver("Pedro");

        var handler = new RequestTicketChatAssistedActionCommandHandler(repo, publisher, resolver);
        var result = await handler.Handle(new RequestTicketChatAssistedActionCommand(session.Id, "request_screenshot", Guid.NewGuid(), "Pedro"), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(publisher.Events, Does.Contain("AssistedAction:request_screenshot"));
        Assert.That(publisher.Events, Does.Contain("Message"));
    }

    [Test]
    public async Task Transfer_UpdatesStartedBy_AndNotifiesTarget()
    {
        var session = CreateActiveSession(Guid.NewGuid());
        var targetUserId = Guid.NewGuid();
        var repo = new StubChatRepo(session);
        var publisher = new RecordingPublisher();
        var notifications = new RecordingNotificationService();
        var resolver = new FixedNameResolver("Técnico N2");

        var handler = new TransferTicketChatSessionCommandHandler(
            repo, resolver, publisher, notifications,
            NullLogger<TransferTicketChatSessionCommandHandler>.Instance);

        var result = await handler.Handle(new TransferTicketChatSessionCommand(session.Id, targetUserId, session.StartedByUserId, "N1"), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(session.StartedByUserId, Is.EqualTo(targetUserId));
        Assert.That(session.StartedByName, Is.EqualTo("Técnico N2"));
        Assert.That(notifications.SentRecipientIds, Does.Contain(targetUserId));
    }

    [Test]
    public async Task RetryInvite_PersistsSessionBeforeDispatchingCommand()
    {
        var session = new TicketChatSession
        {
            Id = Guid.NewGuid(),
            TicketId = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            Status = TicketChatSessionStatus.Pending,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            InviteExpiresAt = DateTime.UtcNow.AddMinutes(3),
        };

        var order = new List<string>();
        var repo = new StubChatRepo(session, () => order.Add("update"));
        var dispatcher = new RecordingCommandDispatcher(() => order.Add("dispatch"));

        var handler = new RetryTicketChatInviteCommandHandler(
            repo, new StubTicketQueryService(null), dispatcher,
            Options.Create(new TicketChatOptions { InviteTimeoutSeconds = 300, InviteRetryCooldownSeconds = 0 }));

        var result = await handler.Handle(new RetryTicketChatInviteCommand(session.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(order, Is.EqualTo(new[] { "update", "dispatch" }));
    }

    [Test]
    public async Task LinkSupportChat_ExistingTicket_UpdatesSessionTicketId()
    {
        var session = CreateActiveSession(ticketId: null);
        var ticketId = Guid.NewGuid();
        var repo = new StubChatRepo(session);
        var ticketQuery = new StubTicketQueryService(new TicketDetailDto(
            Id: ticketId,
            ClientId: session.ClientId,
            SiteId: session.SiteId,
            AgentId: session.AgentId,
            Title: "Chamado Existente",
            Description: "",
            Category: null,
            Priority: TicketPriority.Medium,
            WorkflowStateId: Guid.NewGuid(),
            AssignedToUserId: null,
            SlaExpiresAt: null,
            SlaBreached: false,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            ClosedAt: null,
            DaysOpen: 0));
        var publisher = new RecordingPublisher();

        var handler = new LinkSupportChatToTicketCommandHandler(
            repo, ticketQuery, new NoopMediator(), new NoopActivityLog(), publisher, new FixedNameResolver("Pedro"));

        var result = await handler.Handle(new LinkSupportChatToTicketCommand(
            session.Id, ticketId, null, null, TicketPriority.Medium, null, session.StartedByUserId, "Pedro"), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(session.TicketId, Is.EqualTo(ticketId));
        Assert.That(repo.UpdatedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task LinkSupportChat_CrossTenantTicket_ReturnsNotFound()
    {
        var session = CreateActiveSession(ticketId: null);
        var ticketId = Guid.NewGuid();
        var otherClientId = Guid.NewGuid();
        var repo = new StubChatRepo(session);
        var ticketQuery = new StubTicketQueryService(new TicketDetailDto(
            Id: ticketId,
            ClientId: otherClientId,
            SiteId: session.SiteId,
            AgentId: session.AgentId,
            Title: "Chamado Outro Cliente",
            Description: "",
            Category: null,
            Priority: TicketPriority.Medium,
            WorkflowStateId: Guid.NewGuid(),
            AssignedToUserId: null,
            SlaExpiresAt: null,
            SlaBreached: false,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            ClosedAt: null,
            DaysOpen: 0));
        var publisher = new RecordingPublisher();

        var handler = new LinkSupportChatToTicketCommandHandler(
            repo, ticketQuery, new NoopMediator(), new NoopActivityLog(), publisher, new FixedNameResolver("Pedro"));

        var result = await handler.Handle(new LinkSupportChatToTicketCommand(
            session.Id, ticketId, null, null, TicketPriority.Medium, null, session.StartedByUserId, "Pedro"), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(session.TicketId, Is.Null);
        Assert.That(repo.UpdatedCount, Is.EqualTo(0));
    }

    private sealed class FixedNameResolver(string name) : IChatUserNameResolver
    {
        public Task<string> ResolveAsync(Guid? userId, string fallback, CancellationToken cancellationToken = default)
            => Task.FromResult(name);
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<Guid?> SentRecipientIds { get; } = [];
        public Task<AppNotification> PublishAsync(NotificationPublishRequest request, CancellationToken cancellationToken = default)
        {
            SentRecipientIds.Add(request.RecipientUserId);
            return Task.FromResult(new AppNotification { Id = Guid.NewGuid(), Title = request.Title, Message = request.Message });
        }
        public Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50)
            => Task.FromResult<IReadOnlyList<AppNotification>>([]);
        public Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null)
            => Task.FromResult(true);
        public Task<bool> DeleteAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null)
            => Task.FromResult(true);
    }

    private sealed class RecordingCommandDispatcher(Action onDispatch) : IAgentCommandDispatcher
    {
        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            onDispatch();
            return Task.FromResult(command);
        }
        public Task<AgentCommandDispatchResult> DispatchWithOutcomeAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            onDispatch();
            return Task.FromResult(new AgentCommandDispatchResult(command, CommandDispatchOutcome.Sent));
        }
    }

    private sealed class StubTicketQueryService(TicketDetailDto? ticket) : ITicketQueryService
    {
        public Task<CursorPageDto<TicketListItemDto>> ListTicketsAsync(TicketFilterQuery filter, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<TicketDetailDto?> GetTicketByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(ticket);
        public Task<IReadOnlyList<TicketAnswerDto>> GetAnswersAsync(Guid ticketId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TicketAnswerDto>>([]);
        public Task<CursorPageDto<TicketCommentDto>> GetCommentsAsync(Guid ticketId, string? cursor, int limit, bool includeInternal = false, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class NoopActivityLog : IActivityLogService
    {
        public Task<TicketActivityLog> LogStateChangeAsync(Guid ticketId, Guid? changedByUserId, Guid oldStateId, Guid newStateId)
            => Task.FromResult(new TicketActivityLog());
        public Task<TicketActivityLog> LogAssignmentAsync(Guid ticketId, Guid? changedByUserId, Guid? oldUserId, Guid? newUserId)
            => Task.FromResult(new TicketActivityLog());
        public Task<TicketActivityLog> LogActivityAsync(Guid ticketId, TicketActivityType type, Guid? changedByUserId, string? oldValue = null, string? newValue = null, string? comment = null)
            => Task.FromResult(new TicketActivityLog());
        public Task<TicketActivityLog> LogPriorityChangeAsync(Guid ticketId, Guid? changedByUserId, string oldPriority, string newPriority)
            => Task.FromResult(new TicketActivityLog());
        public Task<TicketActivityLog> LogDepartmentChangeAsync(Guid ticketId, Guid? changedByUserId, string oldDept, string newDept)
            => Task.FromResult(new TicketActivityLog());
    }

    private sealed class NoopMediator : IMediator
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotImplementedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class RecordingPublisher : ITicketChatEnvelopePublisher
    {
        public List<string> Events { get; } = [];
        public Task PublishSessionOpenedAsync(TicketChatSession session, CancellationToken ct = default) { Events.Add("SessionOpened"); return Task.CompletedTask; }
        public Task PublishMessageToAgentAsync(TicketChatSession session, TicketChatMessage message, CancellationToken ct = default) { Events.Add("Message"); return Task.CompletedTask; }
        public Task PublishSessionEndedAsync(TicketChatSession session, CancellationToken ct = default) { Events.Add("SessionEnded"); return Task.CompletedTask; }
        public Task PublishSessionDeclinedAsync(TicketChatSession session, CancellationToken ct = default) { Events.Add("SessionDeclined"); return Task.CompletedTask; }
        public Task PublishActivitySignalAsync(TicketChatSession session, TicketChatMessage message, CancellationToken ct = default) { Events.Add("ActivitySignal"); return Task.CompletedTask; }
        public Task PublishSessionStatusSignalAsync(TicketChatSession session, CancellationToken ct = default) { Events.Add("StatusSignal"); return Task.CompletedTask; }
        public Task PublishAssistedActionToAgentAsync(TicketChatSession session, string actionType, string? requestedByName, string? label, CancellationToken ct = default) { Events.Add($"AssistedAction:{actionType}"); return Task.CompletedTask; }
    }

    private sealed class StubChatRepo(TicketChatSession session, Action? onUpdate = null) : ITicketChatRepository
    {
        public int UpdatedCount { get; private set; }
        public Task<TicketChatSession?> GetByIdAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(sessionId == session.Id ? session : null);
        public Task<TicketChatSession?> GetCurrentForTicketAsync(Guid ticketId, CancellationToken ct = default) => Task.FromResult<TicketChatSession?>(null);
        public Task<TicketChatSession?> GetCurrentForAgentAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult<TicketChatSession?>(null);
        public Task<TicketChatSession?> GetLatestForTicketAsync(Guid ticketId, CancellationToken ct = default) => Task.FromResult<TicketChatSession?>(null);
        public Task<TicketChatSession?> GetLatestForAgentAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult<TicketChatSession?>(null);
        public Task AddSessionAsync(TicketChatSession s, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateSessionAsync(TicketChatSession s, CancellationToken ct = default) { UpdatedCount++; onUpdate?.Invoke(); return Task.CompletedTask; }
        public Task<IReadOnlyList<TicketChatSession>> GetHistoryAsync(Guid ticketId, int skip, int take, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TicketChatSession>>([]);
        public Task<IReadOnlyList<TicketChatMessage>> GetMessagesAsync(Guid sessionId, long afterSequence, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TicketChatMessage>>([]);
        public Task<(TicketChatMessage Message, bool Created)> AppendMessageAsync(Guid sessionId, TicketChatMessage message, IReadOnlyList<Guid> attachmentIds, CancellationToken ct = default) => Task.FromResult((message, true));
        public Task<IReadOnlyList<TicketChatAttachmentRef>> GetAttachmentsForMessagesAsync(IReadOnlyList<Guid> messageIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TicketChatAttachmentRef>>([]);
        public Task<IReadOnlyList<TicketChatSession>> GetExpiredPendingAsync(DateTime nowUtc, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TicketChatSession>>([]);
        public Task<IReadOnlyList<TicketChatSession>> GetInactiveActiveAsync(DateTime cutoffUtc, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TicketChatSession>>([]);
        public Task<IReadOnlyList<TicketChatSession>> GetActiveForClosedTicketsAsync(int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TicketChatSession>>([]);
    }
}
