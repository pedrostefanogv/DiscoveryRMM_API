using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Transferência de chamado entre departamentos: re-herança do perfil de
/// workflow, recálculo de SLA, validação do destino e auditoria.
/// </summary>
public class TicketCommandServiceDepartmentTransferTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid OtherClientId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid DepartmentA = Guid.NewGuid();
    private static readonly Guid DepartmentB = Guid.NewGuid();
    private static readonly Guid ProfileA = Guid.NewGuid();
    private static readonly Guid ProfileB = Guid.NewGuid();
    private static readonly Guid ProfileC = Guid.NewGuid();

    [Test]
    public async Task UpdateTicketAsync_ShouldReinheritProfileAndRecalculateSla_WhenDepartmentChanges()
    {
        var ticket = CreateTicket(DepartmentA, ProfileA);
        var oldSla = ticket.SlaExpiresAt;
        var repo = new FakeTicketRepository(ticket);
        var activityLog = new FakeActivityLogService();
        var profiles = new FakeWorkflowProfileRepository(DepartmentB, ProfileB, ClientId);
        var sla = new FakeSlaService();
        var departments = new FakeDepartmentService(DepartmentB, ClientId, isActive: true);

        var svc = CreateService(repo, activityLog, profiles, sla, departments);

        var updated = await svc.UpdateTicketAsync(
            ticket.Id, null, null, null,
            departmentId: DepartmentB, workflowProfileId: null,
            assignedToUserId: ticket.AssignedToUserId, category: null,
            changedByUserId: UserId);

        Assert.That(updated.DepartmentId, Is.EqualTo(DepartmentB));
        Assert.That(updated.WorkflowProfileId, Is.EqualTo(ProfileB), "deve herdar o perfil do novo depto");
        Assert.That(updated.SlaExpiresAt, Is.Not.Null.And.Not.EqualTo(oldSla), "SLA deve ser recalculado");
        Assert.That(updated.SlaFirstResponseExpiresAt, Is.Not.Null);
        Assert.That(updated.FirstResponseSlaStartedAt, Is.Not.Null);
        Assert.That(sla.SlaCalls, Is.GreaterThanOrEqualTo(2), "resolução + primeira resposta");

        var deptLog = activityLog.RecordedActivities
            .Single(a => a.Type == TicketActivityType.DepartmentChanged);
        Assert.That(deptLog.ChangedByUserId, Is.EqualTo(UserId), "auditoria deve registrar o autor");
        Assert.That(activityLog.RecordedActivities
            .Any(a => a.Type == TicketActivityType.StateChanged && a.Comment == "Workflow profile changed"),
            Is.True, "troca de perfil deve ser auditada");
    }

    [Test]
    public async Task UpdateTicketAsync_ShouldClearProfileAndSla_WhenNewDepartmentHasNoProfile()
    {
        var ticket = CreateTicket(DepartmentA, ProfileA);
        var repo = new FakeTicketRepository(ticket);
        var activityLog = new FakeActivityLogService();
        var profiles = new FakeWorkflowProfileRepository(null, null, null); // sem perfis para o novo depto
        var sla = new FakeSlaService();
        var departments = new FakeDepartmentService(DepartmentB, ClientId, isActive: true);

        var svc = CreateService(repo, activityLog, profiles, sla, departments);

        var updated = await svc.UpdateTicketAsync(
            ticket.Id, null, null, null,
            departmentId: DepartmentB, workflowProfileId: null,
            assignedToUserId: ticket.AssignedToUserId, category: null,
            changedByUserId: UserId);

        Assert.That(updated.DepartmentId, Is.EqualTo(DepartmentB));
        Assert.That(updated.WorkflowProfileId, Is.Null);
        Assert.That(updated.SlaExpiresAt, Is.Null);
        Assert.That(updated.SlaFirstResponseExpiresAt, Is.Null);
    }

    [Test]
    public void UpdateTicketAsync_ShouldThrow_WhenTargetDepartmentBelongsToAnotherClient()
    {
        var ticket = CreateTicket(DepartmentA, ProfileA);
        var repo = new FakeTicketRepository(ticket);
        var activityLog = new FakeActivityLogService();
        var profiles = new FakeWorkflowProfileRepository(DepartmentB, ProfileB, ClientId);
        var sla = new FakeSlaService();
        var departments = new FakeDepartmentService(DepartmentB, OtherClientId, isActive: true);

        var svc = CreateService(repo, activityLog, profiles, sla, departments);

        Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateTicketAsync(
            ticket.Id, null, null, null,
            departmentId: DepartmentB, workflowProfileId: null,
            assignedToUserId: ticket.AssignedToUserId, category: null));
    }

    [Test]
    public void UpdateTicketAsync_ShouldThrow_WhenTargetDepartmentIsInactive()
    {
        var ticket = CreateTicket(DepartmentA, ProfileA);
        var repo = new FakeTicketRepository(ticket);
        var profiles = new FakeWorkflowProfileRepository(DepartmentB, ProfileB, ClientId);
        var departments = new FakeDepartmentService(DepartmentB, ClientId, isActive: false);

        var svc = CreateService(repo, new FakeActivityLogService(), profiles, new FakeSlaService(), departments);

        Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateTicketAsync(
            ticket.Id, null, null, null,
            departmentId: DepartmentB, workflowProfileId: null,
            assignedToUserId: ticket.AssignedToUserId, category: null));
    }

    [Test]
    public async Task UpdateTicketAsync_ShouldHonorExplicitProfile_WhenDepartmentChanges()
    {
        var ticket = CreateTicket(DepartmentA, ProfileA);
        var oldSla = ticket.SlaExpiresAt;
        var repo = new FakeTicketRepository(ticket);
        var activityLog = new FakeActivityLogService();
        var profiles = new FakeWorkflowProfileRepository(DepartmentB, ProfileB, ClientId);
        var sla = new FakeSlaService();
        var departments = new FakeDepartmentService(DepartmentB, ClientId, isActive: true);

        var svc = CreateService(repo, activityLog, profiles, sla, departments);

        var updated = await svc.UpdateTicketAsync(
            ticket.Id, null, null, null,
            departmentId: DepartmentB, workflowProfileId: ProfileC,
            assignedToUserId: ticket.AssignedToUserId, category: null,
            changedByUserId: UserId);

        Assert.That(updated.WorkflowProfileId, Is.EqualTo(ProfileC), "perfil explícito tem precedência");
        Assert.That(updated.SlaExpiresAt, Is.EqualTo(oldSla), "SLA não é recalculado no ramo explícito");
        Assert.That(sla.SlaCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task UpdateTicketAsync_ShouldAdoptDepartmentDefaultProfile_WhenDepartmentResentWithoutProfile()
    {
        // Correção B: chamado criado antes de o departamento ter SLA (sem perfil).
        // Reenviar o MESMO departamento no update deve adotar o default do depto.
        var ticket = CreateTicket(DepartmentA, ProfileA);
        ticket.WorkflowProfileId = null;
        ticket.SlaExpiresAt = null;
        ticket.SlaFirstResponseExpiresAt = null;

        var repo = new FakeTicketRepository(ticket);
        var activityLog = new FakeActivityLogService();
        var profiles = new FakeWorkflowProfileRepository(DepartmentA, ProfileB, ClientId);
        var sla = new FakeSlaService();
        var departments = new FakeDepartmentService(DepartmentA, ClientId, isActive: true);

        var svc = CreateService(repo, activityLog, profiles, sla, departments);

        var updated = await svc.UpdateTicketAsync(
            ticket.Id, null, null, null,
            departmentId: DepartmentA, workflowProfileId: null,
            assignedToUserId: ticket.AssignedToUserId, category: null,
            changedByUserId: UserId);

        Assert.That(updated.WorkflowProfileId, Is.EqualTo(ProfileB), "chamado sem perfil adota o default do departamento");
        Assert.That(updated.SlaExpiresAt, Is.Not.Null, "SLA deve ser calculado");
        Assert.That(updated.SlaFirstResponseExpiresAt, Is.Not.Null);
        Assert.That(sla.SlaCalls, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task UpdateTicketAsync_ShouldNotReadoptProfile_WhenDepartmentNotSent()
    {
        // Correção B não deve sequestrar updates que não tratam de departamento.
        var ticket = CreateTicket(DepartmentA, ProfileA);
        ticket.WorkflowProfileId = null;
        ticket.SlaExpiresAt = null;

        var repo = new FakeTicketRepository(ticket);
        var activityLog = new FakeActivityLogService();
        var profiles = new FakeWorkflowProfileRepository(DepartmentA, ProfileB, ClientId);
        var sla = new FakeSlaService();
        var departments = new FakeDepartmentService(DepartmentA, ClientId, isActive: true);

        var svc = CreateService(repo, activityLog, profiles, sla, departments);

        var updated = await svc.UpdateTicketAsync(
            ticket.Id, "Novo título", null, null,
            departmentId: null, workflowProfileId: null,
            assignedToUserId: ticket.AssignedToUserId, category: null,
            changedByUserId: UserId);

        Assert.That(updated.Title, Is.EqualTo("Novo título"));
        Assert.That(updated.WorkflowProfileId, Is.Null, "sem departamento no request não há re-herdamento");
        Assert.That(updated.SlaExpiresAt, Is.Null);
        Assert.That(sla.SlaCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task BackfillDepartmentProfileAsync_ShouldApplyProfileToOpenTicketsWithoutProfile()
    {
        // Correção A: perfil criado DEPOIS do chamado não pode deixá-lo sem SLA.
        var ticket = CreateTicket(DepartmentA, ProfileA);
        ticket.WorkflowProfileId = null;
        ticket.SlaExpiresAt = null;
        ticket.SlaFirstResponseExpiresAt = null;

        var repo = new FakeTicketRepository(ticket);
        var activityLog = new FakeActivityLogService();
        var sla = new FakeSlaService();
        var svc = CreateService(
            repo, activityLog,
            new FakeWorkflowProfileRepository(null, null, null),
            sla,
            new FakeDepartmentService(DepartmentA, ClientId, isActive: true));

        var profile = new WorkflowProfile
        {
            Id = ProfileB,
            DepartmentId = DepartmentA,
            ClientId = ClientId,
            Name = "96hrs",
            IsActive = true,
        };

        var applied = await svc.BackfillDepartmentProfileAsync(profile, changedByUserId: UserId);

        Assert.That(applied, Is.EqualTo(1));
        Assert.That(ticket.WorkflowProfileId, Is.EqualTo(ProfileB));
        Assert.That(ticket.SlaExpiresAt, Is.Not.Null);
        Assert.That(ticket.FirstResponseSlaStartedAt, Is.Not.Null, "início da contagem de FRT deve ser gravado");
        Assert.That(activityLog.RecordedActivities.Any(a =>
                a.Type == TicketActivityType.StateChanged
                && a.Comment == "SLA do departamento aplicado retroativamente"),
            Is.True, "backfill deve ser auditado");
    }

    [Test]
    public async Task BackfillDepartmentProfileAsync_ShouldSkipInactiveProfile()
    {
        var ticket = CreateTicket(DepartmentA, ProfileA);
        ticket.WorkflowProfileId = null;

        var repo = new FakeTicketRepository(ticket);
        var svc = CreateService(
            repo, new FakeActivityLogService(),
            new FakeWorkflowProfileRepository(null, null, null),
            new FakeSlaService(),
            new FakeDepartmentService(DepartmentA, ClientId, isActive: true));

        var profile = new WorkflowProfile { Id = ProfileB, DepartmentId = DepartmentA, Name = "inativo", IsActive = false };

        var applied = await svc.BackfillDepartmentProfileAsync(profile);

        Assert.That(applied, Is.EqualTo(0));
        Assert.That(ticket.WorkflowProfileId, Is.Null);
    }

    private static Ticket CreateTicket(Guid departmentId, Guid profileId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        Title = "Chamado de teste",
        Description = "Descrição",
        Priority = TicketPriority.Medium,
        DepartmentId = departmentId,
        WorkflowProfileId = profileId,
        WorkflowStateId = Guid.NewGuid(),
        SlaExpiresAt = DateTime.UtcNow.AddHours(2),
        SlaFirstResponseExpiresAt = DateTime.UtcNow.AddHours(1),
        CreatedAt = DateTime.UtcNow.AddHours(-1),
        UpdatedAt = DateTime.UtcNow.AddHours(-1),
    };

    private static TicketCommandService CreateService(
        ITicketRepository repo,
        IActivityLogService activityLog,
        IWorkflowProfileRepository profiles,
        ISlaService sla,
        IDepartmentService departments) =>
        new(
            repo,
            activityLog,
            null!,               // INotificationService (não usado sem troca de responsável)
            null!,               // IWorkflowRepository
            profiles,
            sla,
            null!,               // ITicketAutoAssignmentService
            null!,               // IMediator
            null!,               // IUserRepository
            new FakeOverrideTracker(),
            departments);

    // ── Fakes ──

    private sealed class FakeTicketRepository(Ticket ticket) : ITicketRepository
    {
        public Ticket Ticket { get; } = ticket;

        public Task<Ticket?> GetByIdAsync(Guid id) =>
            Task.FromResult<Ticket?>(Ticket.Id == id ? Ticket : null);

        public Task<Ticket> CreateAsync(Ticket t) => Task.FromResult(t);
        public Task UpdateAsync(Ticket t) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
        public Task<IEnumerable<Ticket>> GetByClientIdAsync(Guid clientId, Guid? workflowStateId = null) =>
            Task.FromResult<IEnumerable<Ticket>>(Array.Empty<Ticket>());
        public Task<IEnumerable<Ticket>> GetByAgentIdAsync(Guid agentId, Guid? workflowStateId = null) =>
            Task.FromResult<IEnumerable<Ticket>>(Array.Empty<Ticket>());
        public Task<IEnumerable<Ticket>> GetAllAsync(TicketFilterQuery filter) =>
            Task.FromResult<IEnumerable<Ticket>>(Array.Empty<Ticket>());
        public Task<IReadOnlyList<Ticket>> GetAllPageAsync(TicketFilterQuery filter) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());
        public Task<IEnumerable<TicketComment>> GetCommentsAsync(Guid ticketId) =>
            Task.FromResult<IEnumerable<TicketComment>>(Array.Empty<TicketComment>());
        public Task<IReadOnlyList<TicketComment>> GetCommentsPageAsync(Guid ticketId, string? cursor, int limit) =>
            Task.FromResult<IReadOnlyList<TicketComment>>(Array.Empty<TicketComment>());
        public Task<TicketComment> AddCommentAsync(TicketComment comment) => Task.FromResult(comment);
        public Task<List<Ticket>> GetOpenTicketsWithSlaAsync(int limit = 2000) => Task.FromResult(new List<Ticket>());
        public Task<List<Ticket>> GetOpenWithoutProfileByDepartmentAsync(Guid departmentId, int limit = 500) =>
            Task.FromResult(new List<Ticket> { Ticket }
                .Where(t => t.DepartmentId == departmentId && t.WorkflowProfileId is null && !t.ClosedAt.HasValue)
                .ToList());
        public Task UpdateSlaHoldAsync(Guid id, DateTime? slaHoldStartedAt, int slaPausedSeconds) => Task.CompletedTask;
        public Task UpdateWorkflowStateWithSlaHoldAsync(Guid id, Guid workflowStateId, DateTime? closedAt, DateTime? slaHoldStartedAt, int slaPausedSecondsDelta, bool updateSlaHold) => Task.CompletedTask;
        public Task UpdateFirstRespondedAtAsync(Guid id, DateTime firstRespondedAt) => Task.CompletedTask;
        public Task<TicketKpiResult> GetKpiAsync(Guid? clientId, Guid? departmentId, DateTime? since) =>
            Task.FromResult(EmptyKpi());
        public Task<TicketKpiResult> GetKpiAsync(TicketFilterQuery filter) => Task.FromResult(EmptyKpi());

        private static TicketKpiResult EmptyKpi() =>
            new(0, 0, 0, 0, 0, 0, 0, 0,
                Array.Empty<TicketKpiByAssignee>(), Array.Empty<TicketKpiByDepartment>());
    }

    private sealed class FakeWorkflowProfileRepository(Guid? departmentId, Guid? profileId, Guid? clientId)
        : IWorkflowProfileRepository
    {
        public Task<List<WorkflowProfile>> GetByDepartmentAsync(Guid departmentIdArg)
        {
            if (departmentIdArg != departmentId || profileId is null)
                return Task.FromResult(new List<WorkflowProfile>());

            return Task.FromResult(new List<WorkflowProfile>
            {
                new() { Id = profileId.Value, DepartmentId = departmentId.Value, ClientId = clientId, Name = "Perfil do depto" }
            });
        }

        public Task<WorkflowProfile?> GetByIdAsync(Guid id) => Task.FromResult<WorkflowProfile?>(null);
        public Task<List<WorkflowProfile>> GetGlobalAsync() => Task.FromResult(new List<WorkflowProfile>());
        public Task<List<WorkflowProfile>> GetByClientAsync(Guid? clientId, bool includeGlobal = true) =>
            Task.FromResult(new List<WorkflowProfile>());
        public Task<WorkflowProfile?> GetDefaultByDepartmentAsync(Guid departmentId) =>
            Task.FromResult<WorkflowProfile?>(null);
        public Task<WorkflowProfile> CreateAsync(WorkflowProfile profile) => throw new NotImplementedException();
        public Task<WorkflowProfile> UpdateAsync(WorkflowProfile profile) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task<int> CountBySlaCalendarIdAsync(Guid slaCalendarId) => Task.FromResult(0);
    }

    private sealed class FakeSlaService : ISlaService
    {
        public int SlaCalls { get; private set; }

        public Task<DateTime> CalculateSlaExpiryAsync(Guid workflowProfileId, DateTime createdAt)
        {
            SlaCalls++;
            return Task.FromResult(createdAt.AddHours(24));
        }

        public Task<DateTime> CalculateFirstResponseExpiryAsync(Guid workflowProfileId, DateTime createdAt)
        {
            SlaCalls++;
            return Task.FromResult(createdAt.AddHours(4));
        }

        public Task<(int HoursRemaining, double PercentUsed, bool Breached)> GetSlaStatusAsync(Guid ticketId) =>
            Task.FromResult((24, 0d, false));
        public Task<(int HoursRemaining, double PercentUsed, bool Breached, bool Achieved)> GetFrtStatusAsync(Guid ticketId) =>
            Task.FromResult((4, 0d, false, false));
        public (int HoursRemaining, double PercentUsed, bool Breached) GetSlaStatus(Ticket ticket, SlaCalendar? calendar) =>
            (24, 0d, false);
        public Task<TicketSlaContext> GetSlaContextForTicketAsync(Ticket ticket) =>
            Task.FromResult(new TicketSlaContext(null, ISlaService.DefaultWarningThresholdPercent));
        public DateTime? GetEffectiveSlaExpiry(Ticket ticket) => ticket.SlaExpiresAt;
        public Task<bool> CheckAndLogSlaBreachAsync(Guid ticketId) => Task.FromResult(false);
        public Task<bool> CheckAndLogSlaBreachAsync(Ticket ticket) => Task.FromResult(false);
    }

    private sealed class FakeDepartmentService(Guid id, Guid? clientId, bool isActive) : IDepartmentService
    {
        public Task<Department?> GetByIdAsync(Guid departmentId, CancellationToken ct = default) =>
            Task.FromResult<Department?>(departmentId == id
                ? new Department { Id = id, ClientId = clientId, Name = "Depto", IsActive = isActive }
                : null);

        public Task<IReadOnlyList<Department>> GetByClientAsync(Guid clientIdArg, bool includeGlobal = true, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Department>>(Array.Empty<Department>());
        public Task<IReadOnlyList<Department>> GetGlobalAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Department>>(Array.Empty<Department>());
        public Task<Department> CreateAsync(Department department, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Department> UpdateAsync(Department department, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeOverrideTracker : ITicketAssignmentOverrideTracker
    {
        public Task<bool> MarkIfOverriddenAsync(Guid ticketId, Guid? newAssigneeUserId, Guid? changedByUserId, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeActivityLogService : IActivityLogService
    {
        public List<TicketActivityLog> RecordedActivities { get; } = [];

        private Task<TicketActivityLog> Record(Guid ticketId, TicketActivityType type, Guid? changedByUserId,
            string? oldValue = null, string? newValue = null, string? comment = null)
        {
            var log = new TicketActivityLog
            {
                TicketId = ticketId,
                Type = type,
                ChangedByUserId = changedByUserId,
                OldValue = oldValue,
                NewValue = newValue,
                Comment = comment,
                CreatedAt = DateTime.UtcNow,
            };
            RecordedActivities.Add(log);
            return Task.FromResult(log);
        }

        public Task<TicketActivityLog> LogActivityAsync(Guid ticketId, TicketActivityType type, Guid? changedByUserId, string? oldValue, string? newValue, string? comment)
            => Record(ticketId, type, changedByUserId, oldValue, newValue, comment);
        public Task<TicketActivityLog> LogStateChangeAsync(Guid ticketId, Guid? changedByUserId, Guid oldStateId, Guid newStateId)
            => Record(ticketId, TicketActivityType.StateChanged, changedByUserId, oldStateId.ToString(), newStateId.ToString());
        public Task<TicketActivityLog> LogAssignmentAsync(Guid ticketId, Guid? changedByUserId, Guid? oldUserId, Guid? newUserId)
            => Record(ticketId, TicketActivityType.Assigned, changedByUserId, oldUserId?.ToString(), newUserId?.ToString());
        public Task<TicketActivityLog> LogPriorityChangeAsync(Guid ticketId, Guid? changedByUserId, string oldPriority, string newPriority)
            => Record(ticketId, TicketActivityType.PriorityChanged, changedByUserId, oldPriority, newPriority);
        public Task<TicketActivityLog> LogDepartmentChangeAsync(Guid ticketId, Guid? changedByUserId, string oldDept, string newDept)
            => Record(ticketId, TicketActivityType.DepartmentChanged, changedByUserId, oldDept, newDept);
    }
}
