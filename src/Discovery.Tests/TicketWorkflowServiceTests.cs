using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Testa TicketWorkflowService — transições de estado, SLA hold/pause, alertas.
/// </summary>
public class TicketWorkflowServiceTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid OldStateId = Guid.NewGuid();
    private static readonly Guid NewStateId = Guid.NewGuid();

    [Test]
    public async Task TransitionAsync_ShouldUpdateWorkflowState_WhenTransitionIsValid()
    {
        var ticket = CreateTicket();
        var repo = new FakeTicketRepositorySingle(ticket);
        var workflowRepo = new FakeWorkflowRepositorySimple(OldStateId, NewStateId, isValid: true);
        var slaService = new FakeSlaService();
        var activityLog = new FakeActivityLogService();
        var alertRuleRepo = new FakeTicketAlertRuleRepository();
        var notificationService = new FakeNotificationService();

        var svc = new TicketWorkflowService(
            repo, workflowRepo, slaService, activityLog,
            alertRuleRepo, new FakeAlertDispatchService(),
            notificationService, NullLogger<TicketWorkflowService>.Instance);

        var result = await svc.TransitionAsync(ticket.Id, NewStateId, null);

        Assert.That(result, Is.Not.Null);
        Assert.That(activityLog.RecordedActivities, Has.Count.GreaterThanOrEqualTo(1));
        Assert.That(activityLog.RecordedActivities[0].Type, Is.EqualTo(TicketActivityType.StateChanged));
    }

    [Test]
    public void TransitionAsync_ShouldThrow_WhenTicketNotFound()
    {
        var repo = new FakeTicketRepositorySingle(null);
        var workflowRepo = new FakeWorkflowRepositorySimple(OldStateId, NewStateId, true);
        var svc = new TicketWorkflowService(
            repo, workflowRepo, new FakeSlaService(), new FakeActivityLogService(),
            new FakeTicketAlertRuleRepository(), new FakeAlertDispatchService(),
            new FakeNotificationService(), NullLogger<TicketWorkflowService>.Instance);

        Assert.ThrowsAsync<InvalidOperationException>(() => svc.TransitionAsync(Guid.NewGuid(), NewStateId, null));
    }

    [Test]
    public void TransitionAsync_ShouldThrow_WhenTransitionIsInvalid()
    {
        var ticket = CreateTicket();
        var repo = new FakeTicketRepositorySingle(ticket);
        var workflowRepo = new FakeWorkflowRepositorySimple(OldStateId, NewStateId, isValid: false);
        var svc = new TicketWorkflowService(
            repo, workflowRepo, new FakeSlaService(), new FakeActivityLogService(),
            new FakeTicketAlertRuleRepository(), new FakeAlertDispatchService(),
            new FakeNotificationService(), NullLogger<TicketWorkflowService>.Instance);

        Assert.ThrowsAsync<InvalidOperationException>(() => svc.TransitionAsync(ticket.Id, NewStateId, null));
    }

    [Test]
    public async Task TransitionAsync_ShouldPauseSla_WhenNewStatePausesSla()
    {
        var ticket = CreateTicket();
        var repo = new FakeTicketRepositorySingle(ticket);
        var workflowRepo = new FakeWorkflowRepositorySimple(
            OldStateId, NewStateId, true, oldPausesSla: false, newPausesSla: true);
        var slaService = new FakeSlaService();
        var activityLog = new FakeActivityLogService();

        var svc = new TicketWorkflowService(
            repo, workflowRepo, slaService, activityLog,
            new FakeTicketAlertRuleRepository(), new FakeAlertDispatchService(),
            new FakeNotificationService(), NullLogger<TicketWorkflowService>.Instance);

        await svc.TransitionAsync(ticket.Id, NewStateId, null);

        Assert.That(repo.SlaHoldStartedAt.HasValue, Is.True);
    }

    [Test]
    public async Task TransitionAsync_ShouldResumeSla_WhenLeavingPauseState()
    {
        var ticket = CreateTicket();
        ticket.SlaHoldStartedAt = DateTime.UtcNow.AddMinutes(-10);
        ticket.SlaPausedSeconds = 100;

        var repo = new FakeTicketRepositorySingle(ticket);
        var workflowRepo = new FakeWorkflowRepositorySimple(
            OldStateId, NewStateId, true, oldPausesSla: true, newPausesSla: false);
        var slaService = new FakeSlaService();

        var svc = new TicketWorkflowService(
            repo, workflowRepo, slaService, new FakeActivityLogService(),
            new FakeTicketAlertRuleRepository(), new FakeAlertDispatchService(),
            new FakeNotificationService(), NullLogger<TicketWorkflowService>.Instance);

        await svc.TransitionAsync(ticket.Id, NewStateId, null);

        Assert.That(repo.SlaHoldStartedAt, Is.Null);
        Assert.That(repo.SlaPausedSeconds, Is.GreaterThan(100));
    }

    [Test]
    public async Task TransitionAsync_ShouldNotifyAssignee_WhenAssigned()
    {
        var ticket = CreateTicket();
        ticket.AssignedToUserId = Guid.NewGuid();
        var repo = new FakeTicketRepositorySingle(ticket);
        var workflowRepo = new FakeWorkflowRepositorySimple(OldStateId, NewStateId, true);
        var notificationService = new FakeNotificationService();

        var svc = new TicketWorkflowService(
            repo, workflowRepo, new FakeSlaService(), new FakeActivityLogService(),
            new FakeTicketAlertRuleRepository(), new FakeAlertDispatchService(),
            notificationService, NullLogger<TicketWorkflowService>.Instance);

        await svc.TransitionAsync(ticket.Id, NewStateId, null);

        Assert.That(notificationService.PublishedNotifications, Has.Count.EqualTo(1));
        Assert.That(notificationService.PublishedNotifications[0].EventType, Is.EqualTo("ticket.state_changed"));
    }

    private static Ticket CreateTicket()
    {
        return new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            WorkflowStateId = OldStateId,
            Title = "Test Ticket",
            Description = "Test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    // ── Fakes ──

    private sealed class FakeTicketRepositorySingle : ITicketRepository
    {
        private readonly Ticket? _ticket;
        public DateTime? SlaHoldStartedAt;
        public int SlaPausedSeconds;

        public FakeTicketRepositorySingle(Ticket? ticket) => _ticket = ticket;

        public Task<Ticket?> GetByIdAsync(Guid id) =>
            Task.FromResult(_ticket?.Id == id ? _ticket : null);

        public Task<IEnumerable<Ticket>> GetByClientIdAsync(Guid clientId, Guid? workflowStateId = null) =>
            Task.FromResult<IEnumerable<Ticket>>(Array.Empty<Ticket>());

        public Task<IEnumerable<Ticket>> GetByAgentIdAsync(Guid agentId, Guid? workflowStateId = null) =>
            Task.FromResult<IEnumerable<Ticket>>(Array.Empty<Ticket>());

        public Task<IEnumerable<Ticket>> GetAllAsync(TicketFilterQuery filter) =>
            Task.FromResult<IEnumerable<Ticket>>(Array.Empty<Ticket>());

        public Task<IReadOnlyList<Ticket>> GetAllPageAsync(TicketFilterQuery filter) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());

        public Task<Ticket> CreateAsync(Ticket ticket) => Task.FromResult(ticket);
        public Task UpdateAsync(Ticket ticket) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;

        public Task UpdateWorkflowStateAsync(Guid id, Guid workflowStateId, DateTime? closedAt = null)
        {
            if (_ticket is not null) _ticket.WorkflowStateId = workflowStateId;
            return Task.CompletedTask;
        }

        public Task<IEnumerable<TicketComment>> GetCommentsAsync(Guid ticketId) =>
            Task.FromResult<IEnumerable<TicketComment>>(Array.Empty<TicketComment>());
        public Task<IReadOnlyList<TicketComment>> GetCommentsPageAsync(Guid ticketId, string? cursor, int limit) =>
            Task.FromResult<IReadOnlyList<TicketComment>>(Array.Empty<TicketComment>());
        public Task<TicketComment> AddCommentAsync(TicketComment comment) => Task.FromResult(comment);
        public Task<List<Ticket>> GetOpenTicketsWithSlaAsync() => Task.FromResult(new List<Ticket>());

        public Task UpdateSlaHoldAsync(Guid id, DateTime? slaHoldStartedAt, int slaPausedSeconds)
        {
            SlaHoldStartedAt = slaHoldStartedAt;
            SlaPausedSeconds = slaPausedSeconds;
            return Task.CompletedTask;
        }

        public Task UpdateFirstRespondedAtAsync(Guid id, DateTime firstRespondedAt) => Task.CompletedTask;

        public Task<TicketKpiResult> GetKpiAsync(Guid? clientId, Guid? departmentId, DateTime? since) =>
            Task.FromResult(new TicketKpiResult(0, 0, 0, 0, 0, 0, 0, 0,
                Array.Empty<TicketKpiByAssignee>(), Array.Empty<TicketKpiByDepartment>()));
    }

    private sealed class FakeWorkflowRepositorySimple : IWorkflowRepository
    {
        private readonly bool _isValid;
        private readonly WorkflowState _oldState;
        private readonly WorkflowState _newState;

        public FakeWorkflowRepositorySimple(Guid oldId, Guid newId, bool isValid,
            bool oldPausesSla = false, bool newPausesSla = false)
        {
            _isValid = isValid;
            _oldState = new WorkflowState { Id = oldId, Name = "Old", PausesSla = oldPausesSla };
            _newState = new WorkflowState { Id = newId, Name = "New", PausesSla = newPausesSla, IsFinal = true };
        }

        public Task<bool> IsTransitionValidAsync(Guid fromStateId, Guid toStateId, Guid clientId) =>
            Task.FromResult(_isValid);
        public Task<WorkflowState?> GetStateByIdAsync(Guid id) =>
            Task.FromResult<WorkflowState?>(id == _oldState.Id ? _oldState : id == _newState.Id ? _newState : null);
        public Task<WorkflowState?> GetInitialStateAsync(Guid clientId) =>
            Task.FromResult<WorkflowState?>(_oldState);
        public Task<IEnumerable<WorkflowState>> GetByClientAsync(Guid clientId) =>
            Task.FromResult<IEnumerable<WorkflowState>>(new[] { _oldState, _newState });
        public Task<WorkflowState> CreateAsync(WorkflowState state) => throw new NotImplementedException();
        public Task UpdateAsync(WorkflowState state) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task<IEnumerable<WorkflowTransition>> GetTransitionsByFromStateAsync(Guid fromStateId) =>
            Task.FromResult<IEnumerable<WorkflowTransition>>(Array.Empty<WorkflowTransition>());
        public Task<WorkflowTransition> CreateTransitionAsync(WorkflowTransition transition) => throw new NotImplementedException();
        public Task DeleteTransitionAsync(Guid transitionId) => throw new NotImplementedException();
    }

    private sealed class FakeSlaService : ISlaService
    {
        public Task<DateTime> CalculateSlaExpiryAsync(Guid workflowProfileId, DateTime createdAt) =>
            Task.FromResult(createdAt.AddHours(24));
        public Task<DateTime> CalculateFirstResponseExpiryAsync(Guid workflowProfileId, DateTime createdAt) =>
            Task.FromResult(createdAt.AddHours(4));
        public Task<(int HoursRemaining, double PercentUsed, bool Breached)> GetSlaStatusAsync(Guid ticketId) =>
            Task.FromResult((24, 0d, false));
        public Task<(int HoursRemaining, double PercentUsed, bool Breached, bool Achieved)> GetFrtStatusAsync(Guid ticketId) =>
            Task.FromResult((4, 0d, false, false));
        public DateTime? GetEffectiveSlaExpiry(Ticket ticket) => ticket.SlaExpiresAt;
        public Task<bool> CheckAndLogSlaBreachAsync(Guid ticketId) => Task.FromResult(false);
    }

    private sealed class FakeActivityLogService : IActivityLogService
    {
        public List<TicketActivityLog> RecordedActivities { get; } = [];

        public Task LogActivityAsync(Guid ticketId, TicketActivityType type, Guid? changedByUserId, string? oldValue, string? newValue, string? comment)
        {
            RecordedActivities.Add(new TicketActivityLog { TicketId = ticketId, Type = type, CreatedAt = DateTime.UtcNow });
            return Task.CompletedTask;
        }

        public Task LogStateChangeAsync(Guid ticketId, Guid? changedByUserId, Guid oldStateId, Guid newStateId) =>
            LogActivityAsync(ticketId, TicketActivityType.StateChanged, changedByUserId, oldStateId.ToString(), newStateId.ToString(), null);
        public Task LogAssignmentAsync(Guid ticketId, Guid? changedByUserId, Guid? oldAssignee, Guid? newAssignee) =>
            LogActivityAsync(ticketId, TicketActivityType.Assigned, changedByUserId, oldAssignee?.ToString(), newAssignee?.ToString(), null);
        public Task LogPriorityChangeAsync(Guid ticketId, Guid? changedByUserId, string oldPriority, string newPriority) =>
            LogActivityAsync(ticketId, TicketActivityType.PriorityChanged, changedByUserId, oldPriority, newPriority, null);
        public Task LogDepartmentChangeAsync(Guid ticketId, Guid? changedByUserId, string oldDept, string newDept) =>
            LogActivityAsync(ticketId, TicketActivityType.DepartmentChanged, changedByUserId, oldDept, newDept, null);
    }

    private sealed class FakeTicketAlertRuleRepository : ITicketAlertRuleRepository
    {
        public Task<IEnumerable<TicketAlertRule>> GetByWorkflowStateIdAsync(Guid workflowStateId) =>
            Task.FromResult<IEnumerable<TicketAlertRule>>(Array.Empty<TicketAlertRule>());
        public Task<TicketAlertRule?> GetByIdAsync(Guid id) => Task.FromResult<TicketAlertRule?>(null);
        public Task<IEnumerable<TicketAlertRule>> GetAllAsync() =>
            Task.FromResult<IEnumerable<TicketAlertRule>>(Array.Empty<TicketAlertRule>());
        public Task<TicketAlertRule> CreateAsync(TicketAlertRule rule) => throw new NotImplementedException();
        public Task UpdateAsync(TicketAlertRule rule) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
    }

    private sealed class FakeAlertDispatchService : IAlertDispatchService
    {
        public Task DispatchAsync(AgentAlertDefinition alertDefinition, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public List<NotificationPublishRequest> PublishedNotifications { get; } = [];

        public Task PublishAsync(NotificationPublishRequest notification, CancellationToken ct = default)
        {
            PublishedNotifications.Add(notification);
            return Task.CompletedTask;
        }
    }
}
