using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Tickets.CommandHandlers;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Regressões das melhorias de suporte: relations, reopen e rating (CSAT).
/// Usa DiscoveryDbContext em memória com um subconjunto de entidades.
/// </summary>
public class TicketSupportEnhancementsTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"support-enhancements-{Guid.NewGuid():N}")
            .Options;
        return new SupportTestDbContext(options);
    }

    private static Ticket NewTicket(Guid clientId, Guid stateId, Guid? profileId = null)
    {
        var now = DateTime.UtcNow;
        return new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Title = "Chamado",
            Description = "Descrição",
            WorkflowStateId = stateId,
            WorkflowProfileId = profileId,
            Priority = TicketPriority.Medium,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ActivityLogService BuildActivityLog(DiscoveryDbContext db)
        => new(new TicketActivityLogRepository(db), NullLogger<ActivityLogService>.Instance);

    private static SlaService BuildSlaService(DiscoveryDbContext db)
        => new(new WorkflowProfileRepository(db),
               new TicketRepository(db, new NoopAgentMessaging()),
               BuildActivityLog(db),
               new SlaCalendarRepository(db),
               NullLogger<SlaService>.Instance);

    [Test]
    public async Task RateTicket_RejectsOutOfRangeRating()
    {
        await using var db = CreateDb();
        var handler = new RateTicketCommandHandler(
            new TicketRepository(db, new NoopAgentMessaging()),
            new WorkflowRepository(db),
            BuildActivityLog(db),
            NullLogger<RateTicketCommandHandler>.Instance);

        var result = await handler.Handle(new RateTicketCommand(Guid.NewGuid(), 9, null, null, "tester"), default);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
    }

    [Test]
    public async Task CreateRelation_RejectsSelfRelation()
    {
        await using var db = CreateDb();
        var handler = new CreateTicketRelationCommandHandler(
            new TicketRelationRepository(db), db, BuildActivityLog(db));

        var id = Guid.NewGuid();
        var result = await handler.Handle(
            new CreateTicketRelationCommand(id, id, TicketRelationType.RelatesTo, null, "tester"), default);

        Assert.That(result.IsFailure, Is.True);
    }

    [Test]
    public async Task CreateRelation_PersistsAndBlocksDuplicateAndReverse()
    {
        await using var db = CreateDb();
        var client = new Client { Id = Guid.NewGuid(), Name = "Cliente", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var state = new WorkflowState { Id = Guid.NewGuid(), Name = "Open", IsInitial = true, SortOrder = 1 };
        var ticketA = NewTicket(client.Id, state.Id);
        var ticketB = NewTicket(client.Id, state.Id);
        db.AddRange(client, state, ticketA, ticketB);
        await db.SaveChangesAsync();

        var handler = new CreateTicketRelationCommandHandler(
            new TicketRelationRepository(db), db, BuildActivityLog(db));

        var first = await handler.Handle(
            new CreateTicketRelationCommand(ticketA.Id, ticketB.Id, TicketRelationType.RelatesTo, null, "tester"), default);
        Assert.That(first.IsSuccess, Is.True);

        var duplicate = await handler.Handle(
            new CreateTicketRelationCommand(ticketA.Id, ticketB.Id, TicketRelationType.RelatesTo, null, "tester"), default);
        Assert.That(duplicate.IsFailure, Is.True);

        var reverse = await handler.Handle(
            new CreateTicketRelationCommand(ticketB.Id, ticketA.Id, TicketRelationType.Blocks, null, "tester"), default);
        Assert.That(reverse.IsFailure, Is.True);

        Assert.That(await db.TicketRelations.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Reopen_RejectsTicketThatIsNotClosed()
    {
        await using var db = CreateDb();
        var client = new Client { Id = Guid.NewGuid(), Name = "Cliente", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var open = new WorkflowState { Id = Guid.NewGuid(), Name = "Open", IsInitial = true, SortOrder = 1 };
        var ticket = NewTicket(client.Id, open.Id);
        db.AddRange(client, open, ticket);
        await db.SaveChangesAsync();

        var handler = new ReopenTicketCommandHandler(
            new TicketRepository(db, new NoopAgentMessaging()),
            new WorkflowRepository(db),
            BuildSlaService(db),
            BuildActivityLog(db),
            new NoopNotificationService(),
            NullLogger<ReopenTicketCommandHandler>.Instance);

        var result = await handler.Handle(new ReopenTicketCommand(ticket.Id, "motivo", null), default);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
    }

    [Test]
    public async Task Reopen_ResetsStateSlaAndRating_AndLogs()
    {
        await using var db = CreateDb();
        var client = new Client { Id = Guid.NewGuid(), Name = "Cliente", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var dept = new Department { Id = Guid.NewGuid(), Name = "TI", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var profile = new WorkflowProfile
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            DepartmentId = dept.Id,
            Name = "Default",
            SlaHours = 8,
            FirstResponseSlaHours = 4,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var open = new WorkflowState { Id = Guid.NewGuid(), Name = "Open", IsInitial = true, SortOrder = 1 };
        var closed = new WorkflowState { Id = Guid.NewGuid(), Name = "Closed", IsFinal = true, SortOrder = 9 };
        var ticket = NewTicket(client.Id, closed.Id, profile.Id);
        ticket.ClosedAt = DateTime.UtcNow;
        ticket.Rating = 5;
        ticket.RatingFeedback = "bom";
        ticket.SlaBreached = true;
        db.AddRange(client, dept, profile, open, closed, ticket);
        await db.SaveChangesAsync();

        var handler = new ReopenTicketCommandHandler(
            new TicketRepository(db, new NoopAgentMessaging()),
            new WorkflowRepository(db),
            BuildSlaService(db),
            BuildActivityLog(db),
            new NoopNotificationService(),
            NullLogger<ReopenTicketCommandHandler>.Instance);

        var result = await handler.Handle(new ReopenTicketCommand(ticket.Id, "cliente voltou", null), default);

        Assert.That(result.IsSuccess, Is.True);

        var reloaded = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.That(reloaded.WorkflowStateId, Is.EqualTo(open.Id));
        Assert.That(reloaded.ClosedAt, Is.Null);
        Assert.That(reloaded.Rating, Is.Null);
        Assert.That(reloaded.SlaBreached, Is.False);
        Assert.That(reloaded.SlaExpiresAt, Is.Not.Null);

        var logs = await db.TicketActivityLogs.Where(l => l.TicketId == ticket.Id).ToListAsync();
        Assert.That(logs.Any(l => l.Type == TicketActivityType.Reopened), Is.True);
    }

    [Test]
    public async Task Merge_ReturnsNotFound_WhenTargetMissing()
    {
        await using var db = CreateDb();
        var handler = new MergeTicketsCommandHandler(
            new WorkflowRepository(db), db, NullLogger<MergeTicketsCommandHandler>.Instance);

        var result = await handler.Handle(
            new MergeTicketsCommand(Guid.NewGuid(), new[] { Guid.NewGuid() }, null, null), default);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    private sealed class SupportTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Client), typeof(Department), typeof(WorkflowProfile), typeof(WorkflowState),
                typeof(Ticket), typeof(TicketComment), typeof(TicketActivityLog), typeof(TicketWatcher),
                typeof(TicketRelation), typeof(TicketMergeRecord), typeof(SlaCalendar), typeof(SlaCalendarHoliday)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(e => { e.HasKey(c => c.Id); e.Property(c => c.Name).IsRequired(); });
            modelBuilder.Entity<Department>(e => e.HasKey(d => d.Id));
            modelBuilder.Entity<WorkflowProfile>(e => e.HasKey(p => p.Id));
            modelBuilder.Entity<WorkflowState>(e => e.HasKey(s => s.Id));
            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
            modelBuilder.Entity<TicketComment>(e => e.HasKey(c => c.Id));
            modelBuilder.Entity<TicketActivityLog>(e => e.HasKey(l => l.Id));
            modelBuilder.Entity<TicketWatcher>(e => e.HasKey(w => w.Id));
            modelBuilder.Entity<SlaCalendar>(e => e.HasKey(c => c.Id));
            modelBuilder.Entity<SlaCalendarHoliday>(e => e.HasKey(h => h.Id));
            modelBuilder.Entity<TicketRelation>(e =>
            {
                e.HasKey(r => r.Id);
                e.Ignore(r => r.SourceTicket);
                e.Ignore(r => r.TargetTicket);
            });
            modelBuilder.Entity<TicketMergeRecord>(e =>
            {
                e.HasKey(r => r.Id);
                e.Ignore(r => r.SourceTicket);
                e.Ignore(r => r.TargetTicket);
            });
        }
    }

    private sealed class NoopAgentMessaging : IAgentMessaging
    {
        public bool IsConnected => false;
        public Task SendCommandAsync(Guid agentId, Guid commandId, string commandType, string payload) => Task.CompletedTask;
        public Task PublishSiteFanoutCommandAsync(Guid clientId, Guid siteId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishClientFanoutCommandAsync(Guid clientId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishGlobalFanoutCommandAsync(CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishDashboardEventAsync(DashboardEventMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, Guid overrideClientId, Guid overrideSiteId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendCommandToSubjectAsync(Guid clientId, Guid siteId, Guid agentId, Guid commandId, string commandType, string payload) => Task.CompletedTask;
        public Task SubscribeToAgentMessagesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoopNotificationService : INotificationService
    {
        public Task<AppNotification> PublishAsync(NotificationPublishRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new AppNotification());
        public Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50)
            => Task.FromResult<IReadOnlyList<AppNotification>>(Array.Empty<AppNotification>());
        public Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null) => Task.FromResult(false);
        public Task<bool> DeleteAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null) => Task.FromResult(false);
    }
}
