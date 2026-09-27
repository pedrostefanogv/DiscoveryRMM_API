using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Sanitização do banco: chamados sem estado (legado do create do agent),
/// departamento/perfil órfãos e SLA ausente. Dry-run não pode alterar nada.
/// </summary>
public class DatabaseSanitizationServiceTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid StateId = Guid.NewGuid();
    private static readonly Guid InitialStateId = Guid.NewGuid();

    [Test]
    public async Task RunAsync_WhenTicketHasEmptyState_AppliesInitialStateAndLogs()
    {
        var db = CreateDb();
        db.WorkflowStates.Add(Initial(ClientId));
        var ticket = TicketWithState(Guid.Empty);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var activityLog = new FakeActivityLogService();
        var svc = BuildService(db, activityLog, new FakeWorkflowRepository(InitialStateId));

        var report = await svc.RunAsync(dryRun: false, checks: [DatabaseSanitizationChecks.InvalidWorkflowState]);

        var check = report.Checks.Single();
        Assert.That(check.Scanned, Is.EqualTo(1));
        Assert.That(check.Fixed, Is.EqualTo(1));
        Assert.That((await db.Tickets.SingleAsync()).WorkflowStateId, Is.EqualTo(InitialStateId));
        Assert.That(activityLog.RecordedActivities.Any(a =>
                a.Type == TicketActivityType.StateChanged
                && a.Comment == "Estado inicial reaplicado (sanitização)"),
            Is.True);
    }

    [Test]
    public async Task RunAsync_DryRun_DoesNotChangeData()
    {
        var db = CreateDb();
        var ticket = TicketWithState(Guid.Empty);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var activityLog = new FakeActivityLogService();
        var svc = BuildService(db, activityLog, new FakeWorkflowRepository(InitialStateId));

        var report = await svc.RunAsync(dryRun: true, checks: [DatabaseSanitizationChecks.InvalidWorkflowState]);

        Assert.That(report.DryRun, Is.True);
        Assert.That(report.Checks.Single().Scanned, Is.EqualTo(1));
        Assert.That(report.Checks.Single().Fixed, Is.EqualTo(0));
        Assert.That((await db.Tickets.SingleAsync()).WorkflowStateId, Is.EqualTo(Guid.Empty));
        Assert.That(activityLog.RecordedActivities, Is.Empty);
    }

    [Test]
    public async Task RunAsync_WhenNoInitialState_SkipsWithoutThrowing()
    {
        var db = CreateDb();
        db.Tickets.Add(TicketWithState(Guid.Empty));
        await db.SaveChangesAsync();

        var svc = BuildService(db, new FakeActivityLogService(), new FakeWorkflowRepository(null));

        var report = await svc.RunAsync(dryRun: false, checks: [DatabaseSanitizationChecks.InvalidWorkflowState]);

        var check = report.Checks.Single();
        Assert.That(check.Fixed, Is.EqualTo(0));
        Assert.That(check.Skipped, Is.EqualTo(1));
        Assert.That(check.Error, Is.Null);
    }

    [Test]
    public async Task RunAsync_WhenDepartmentIsOrphan_ClearsReference()
    {
        var db = CreateDb();
        var ticket = TicketWithState(StateId);
        ticket.DepartmentId = Guid.NewGuid(); // departamento inexistente
        db.WorkflowStates.Add(new WorkflowState { Id = StateId, ClientId = ClientId, Name = "Open", IsInitial = true });
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var activityLog = new FakeActivityLogService();
        var svc = BuildService(db, activityLog, new FakeWorkflowRepository(InitialStateId));

        var report = await svc.RunAsync(dryRun: false, checks: [DatabaseSanitizationChecks.OrphanDepartment]);

        Assert.That(report.Checks.Single().Fixed, Is.EqualTo(1));
        Assert.That((await db.Tickets.SingleAsync()).DepartmentId, Is.Null);
        Assert.That(activityLog.RecordedActivities.Any(a => a.Type == TicketActivityType.DepartmentChanged), Is.True);
    }

    [Test]
    public async Task RunAsync_WhenOpenTicketWithoutProfile_AppliesDepartmentDefaultAndSla()
    {
        var db = CreateDb();
        var departmentId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        db.WorkflowStates.Add(new WorkflowState { Id = StateId, ClientId = ClientId, Name = "Open", IsInitial = true });
        db.Departments.Add(new Department { Id = departmentId, Name = "Compras", IsActive = true });
        db.WorkflowProfiles.Add(new WorkflowProfile
        {
            Id = profileId,
            DepartmentId = departmentId,
            Name = "96hrs",
            SlaHours = 96,
            FirstResponseSlaHours = 4,
            IsActive = true,
        });
        var ticket = TicketWithState(StateId);
        ticket.DepartmentId = departmentId;
        ticket.WorkflowProfileId = null;
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var svc = BuildService(
            db, new FakeActivityLogService(),
            new FakeWorkflowRepository(InitialStateId),
            new FakeWorkflowProfileRepository(new WorkflowProfile
            {
                Id = profileId, DepartmentId = departmentId, Name = "96hrs", SlaHours = 96, IsActive = true,
            }));

        var report = await svc.RunAsync(dryRun: false, checks: [DatabaseSanitizationChecks.MissingWorkflowProfile]);

        Assert.That(report.Checks.Single().Fixed, Is.EqualTo(1));
        var updated = await db.Tickets.SingleAsync();
        Assert.That(updated.WorkflowProfileId, Is.EqualTo(profileId));
        Assert.That(updated.SlaExpiresAt, Is.Not.Null);
        Assert.That(updated.SlaFirstResponseExpiresAt, Is.Not.Null);
        Assert.That(updated.FirstResponseSlaStartedAt, Is.Not.Null);
    }

    // ── Helpers ──

    private static WorkflowState Initial(Guid clientId) =>
        new() { Id = InitialStateId, ClientId = clientId, Name = "Open", IsInitial = true };

    private static Ticket TicketWithState(Guid stateId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        Title = "Chamado de teste",
        Description = "d",
        Priority = TicketPriority.Medium,
        WorkflowStateId = stateId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static DatabaseSanitizationService BuildService(
        DiscoveryDbContext db,
        IActivityLogService activityLog,
        IWorkflowRepository workflowRepo,
        IWorkflowProfileRepository? profileRepo = null) =>
        new(
            db,
            workflowRepo,
            profileRepo ?? new FakeWorkflowProfileRepository(null),
            new FakeSlaService(),
            activityLog,
            NullLogger<DatabaseSanitizationService>.Instance);

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("sanitization-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new SanitizationTestDbContext(options);
    }

    private sealed class SanitizationTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Ticket), typeof(Department), typeof(WorkflowState), typeof(WorkflowProfile),
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
            modelBuilder.Entity<Department>(e => e.HasKey(d => d.Id));
            modelBuilder.Entity<WorkflowState>(e => e.HasKey(s => s.Id));
            modelBuilder.Entity<WorkflowProfile>(e => e.HasKey(p => p.Id));
        }
    }

    // ── Fakes ──

    private sealed class FakeWorkflowRepository(Guid? initialId) : IWorkflowRepository
    {
        public Task<WorkflowState?> GetInitialStateAsync(Guid? clientId = null) =>
            Task.FromResult(initialId.HasValue
                ? new WorkflowState { Id = initialId.Value, ClientId = null, Name = "Open", IsInitial = true }
                : null);

        public Task<WorkflowState?> GetStateByIdAsync(Guid id) => Task.FromResult<WorkflowState?>(null);
        public Task<IEnumerable<WorkflowState>> GetStatesAsync(Guid? clientId = null) =>
            Task.FromResult<IEnumerable<WorkflowState>>(Array.Empty<WorkflowState>());
        public Task<WorkflowState> CreateStateAsync(WorkflowState state) => throw new NotImplementedException();
        public Task UpdateStateAsync(WorkflowState state) => throw new NotImplementedException();
        public Task DeleteStateAsync(Guid id) => throw new NotImplementedException();
        public Task<IEnumerable<WorkflowTransition>> GetTransitionsAsync(Guid? clientId = null) =>
            Task.FromResult<IEnumerable<WorkflowTransition>>(Array.Empty<WorkflowTransition>());
        public Task<IEnumerable<WorkflowTransition>> GetTransitionsFromStateAsync(Guid fromStateId, Guid? clientId = null) =>
            Task.FromResult<IEnumerable<WorkflowTransition>>(Array.Empty<WorkflowTransition>());
        public Task<bool> IsTransitionValidAsync(Guid fromStateId, Guid toStateId, Guid? clientId = null) =>
            Task.FromResult(false);
        public Task<WorkflowTransition> CreateTransitionAsync(WorkflowTransition transition) => throw new NotImplementedException();
        public Task DeleteTransitionAsync(Guid id) => throw new NotImplementedException();
    }

    private sealed class FakeWorkflowProfileRepository(WorkflowProfile? defaultProfile) : IWorkflowProfileRepository
    {
        public Task<WorkflowProfile?> GetDefaultByDepartmentAsync(Guid departmentId) =>
            Task.FromResult(defaultProfile);

        public Task<List<WorkflowProfile>> GetByDepartmentAsync(Guid departmentId) =>
            Task.FromResult(new List<WorkflowProfile>());
        public Task<WorkflowProfile?> GetByIdAsync(Guid id) => Task.FromResult<WorkflowProfile?>(null);
        public Task<List<WorkflowProfile>> GetGlobalAsync() => Task.FromResult(new List<WorkflowProfile>());
        public Task<List<WorkflowProfile>> GetByClientAsync(Guid? clientId, bool includeGlobal = true) =>
            Task.FromResult(new List<WorkflowProfile>());
        public Task<WorkflowProfile> CreateAsync(WorkflowProfile profile) => throw new NotImplementedException();
        public Task<WorkflowProfile> UpdateAsync(WorkflowProfile profile) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task<int> CountBySlaCalendarIdAsync(Guid slaCalendarId) => Task.FromResult(0);
    }

    private sealed class FakeSlaService : ISlaService
    {
        public Task<DateTime> CalculateSlaExpiryAsync(Guid workflowProfileId, DateTime createdAt) =>
            Task.FromResult(createdAt.AddHours(96));
        public Task<DateTime> CalculateFirstResponseExpiryAsync(Guid workflowProfileId, DateTime createdAt) =>
            Task.FromResult(createdAt.AddHours(4));
        public Task<(int HoursRemaining, double PercentUsed, bool Breached)> GetSlaStatusAsync(Guid ticketId) =>
            Task.FromResult((96, 0d, false));
        public Task<(int HoursRemaining, double PercentUsed, bool Breached, bool Achieved)> GetFrtStatusAsync(Guid ticketId) =>
            Task.FromResult((4, 0d, false, false));
        public (int HoursRemaining, double PercentUsed, bool Breached) GetSlaStatus(Ticket ticket, SlaCalendar? calendar) =>
            (96, 0d, false);
        public Task<TicketSlaContext> GetSlaContextForTicketAsync(Ticket ticket) =>
            Task.FromResult(new TicketSlaContext(null, ISlaService.DefaultWarningThresholdPercent));
        public DateTime? GetEffectiveSlaExpiry(Ticket ticket) => ticket.SlaExpiresAt;
        public Task<bool> CheckAndLogSlaBreachAsync(Guid ticketId) => Task.FromResult(false);
        public Task<bool> CheckAndLogSlaBreachAsync(Ticket ticket) => Task.FromResult(false);
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
