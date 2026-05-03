using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Testes unitários para o SlaService, incluindo cálculo de horas úteis.
/// </summary>
public class SlaServiceTests
{
    // ── AddWorkingHours (testes unitários puros, sem I/O) ─────────────────

    [Test]
    public void AddWorkingHours_NoCalendar_AddsHoursDirectly()
    {
        var from = new DateTime(2024, 1, 15, 9, 0, 0, DateTimeKind.Utc); // Segunda 09:00 UTC
        var calendar = BuildCalendar("UTC");

        var result = SlaService.AddWorkingHours(from, 8, calendar);

        // 9h + 8h úteis = 17h (dentro do expediente 08-18)
        Assert.That(result, Is.EqualTo(new DateTime(2024, 1, 15, 17, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void AddWorkingHours_SpansMultipleDays()
    {
        // Sexta 16:00 + 4 horas úteis → cruza para segunda
        var from = new DateTime(2024, 1, 19, 16, 0, 0, DateTimeKind.Utc); // Sexta 16:00 UTC
        var calendar = BuildCalendar("UTC");

        var result = SlaService.AddWorkingHours(from, 4, calendar);

        // 2h sexta (16-18) + 2h segunda (08-10) → segunda 10:00
        Assert.That(result, Is.EqualTo(new DateTime(2024, 1, 22, 10, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void AddWorkingHours_SkipsWeekend()
    {
        // Sexta 17:30 + 1h útil → segunda 08:30
        var from = new DateTime(2024, 1, 19, 17, 30, 0, DateTimeKind.Utc);
        var calendar = BuildCalendar("UTC");

        var result = SlaService.AddWorkingHours(from, 1, calendar);

        Assert.That(result, Is.EqualTo(new DateTime(2024, 1, 22, 8, 30, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void AddWorkingHours_SkipsHoliday()
    {
        // Segunda 09:00 + 1h, mas segunda é feriado → terça 09:00+1h = 10:00
        var from = new DateTime(2024, 1, 15, 9, 0, 0, DateTimeKind.Utc);
        var calendar = BuildCalendar("UTC");
        calendar.Holidays.Add(new SlaCalendarHoliday
        {
            Id = Guid.NewGuid(),
            CalendarId = calendar.Id,
            Date = new DateTime(2024, 1, 15),
            Name = "Feriado"
        });

        var result = SlaService.AddWorkingHours(from, 1, calendar);

        Assert.That(result, Is.EqualTo(new DateTime(2024, 1, 16, 9, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void AddWorkingHours_StartBeforeWorkday_StartsAtWorkDayBegin()
    {
        // Segunda 06:00 + 2h = Segunda 10:00 (começa em 08:00)
        var from = new DateTime(2024, 1, 15, 6, 0, 0, DateTimeKind.Utc);
        var calendar = BuildCalendar("UTC");

        var result = SlaService.AddWorkingHours(from, 2, calendar);

        Assert.That(result, Is.EqualTo(new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc)));
    }

    // ── Testes com banco em memória ────────────────────────────────────────

    [Test]
    public async Task GetSlaStatusAsync_NotBreached_ReturnsCorrectHoursRemaining()
    {
        await using var fixture = await CreateFixtureAsync(slaHours: 8);

        var ticket = fixture.Ticket;
        // Ticket criado há 4h, expira em 4h → ~50% usado
        ticket.CreatedAt = DateTime.UtcNow.AddHours(-4);
        ticket.SlaExpiresAt = DateTime.UtcNow.AddHours(4);
        fixture.Db.Tickets.Update(ticket);
        await fixture.Db.SaveChangesAsync();

        var (hoursRemaining, percentUsed, breached) = await fixture.SlaService.GetSlaStatusAsync(ticket.Id);

        Assert.That(breached, Is.False);
        Assert.That(hoursRemaining, Is.GreaterThanOrEqualTo(3));
        Assert.That(percentUsed, Is.InRange(40.0, 55.0)); // ~50% usado
    }

    [Test]
    public async Task GetSlaStatusAsync_Breached_ReturnsBreached()
    {
        await using var fixture = await CreateFixtureAsync(slaHours: 8);

        var ticket = fixture.Ticket;
        ticket.SlaExpiresAt = DateTime.UtcNow.AddHours(-1); // já expirou
        ticket.SlaBreached = true;
        fixture.Db.Tickets.Update(ticket);
        await fixture.Db.SaveChangesAsync();

        var (hoursRemaining, percentUsed, breached) = await fixture.SlaService.GetSlaStatusAsync(ticket.Id);

        Assert.That(breached, Is.True);
    }

    [Test]
    public async Task GetFrtStatusAsync_NotYetResponded_NotAchievedNotBreached()
    {
        await using var fixture = await CreateFixtureAsync(slaHours: 8, frtHours: 4);

        var ticket = fixture.Ticket;
        ticket.SlaFirstResponseExpiresAt = DateTime.UtcNow.AddHours(3); // expira em 3h
        fixture.Db.Tickets.Update(ticket);
        await fixture.Db.SaveChangesAsync();

        var (hoursRemaining, _, breached, achieved) = await fixture.SlaService.GetFrtStatusAsync(ticket.Id);

        Assert.That(breached, Is.False);
        Assert.That(achieved, Is.False);
        Assert.That(hoursRemaining, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task GetFrtStatusAsync_RespondedOnTime_Achieved()
    {
        await using var fixture = await CreateFixtureAsync(slaHours: 8, frtHours: 4);

        var ticket = fixture.Ticket;
        var frtExpiry = DateTime.UtcNow.AddHours(-1);
        ticket.SlaFirstResponseExpiresAt = frtExpiry;
        ticket.FirstRespondedAt = frtExpiry.AddMinutes(-30); // respondeu antes da expiração
        fixture.Db.Tickets.Update(ticket);
        await fixture.Db.SaveChangesAsync();

        var (_, _, breached, achieved) = await fixture.SlaService.GetFrtStatusAsync(ticket.Id);

        Assert.That(achieved, Is.True);
        Assert.That(breached, Is.False);
    }

    [Test]
    public async Task SlaHold_PausesAndResumes_ExpiryShifted()
    {
        await using var fixture = await CreateFixtureAsync(slaHours: 8);

        var ticket = fixture.Ticket;
        var originalExpiry = DateTime.UtcNow.AddHours(8);
        ticket.SlaExpiresAt = originalExpiry;
        ticket.SlaHoldStartedAt = DateTime.UtcNow.AddMinutes(-30); // em pausa há 30 min
        ticket.SlaPausedSeconds = 0;
        fixture.Db.Tickets.Update(ticket);
        await fixture.Db.SaveChangesAsync();

        // GetEffectiveSlaExpiry deve considerar os 30 min de pausa atual
        var effectiveExpiry = fixture.SlaService.GetEffectiveSlaExpiry(ticket);

        Assert.That(effectiveExpiry, Is.Not.Null);
        Assert.That(effectiveExpiry!.Value, Is.GreaterThanOrEqualTo(originalExpiry.AddMinutes(29)));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static SlaCalendar BuildCalendar(string tz) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test Calendar",
        Timezone = tz,
        WorkDayStartHour = 8,
        WorkDayEndHour = 18,
        WorkDaysJson = "[1,2,3,4,5]",
        Holidays = new List<SlaCalendarHoliday>(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static async Task<SlaTestFixture> CreateFixtureAsync(int slaHours = 8, int frtHours = 4)
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"sla-tests-{Guid.NewGuid():N}")
            .Options;

        var db = new SlaTestDiscoveryDbContext(options);

        var now = DateTime.UtcNow;
        var client = new Client { Id = Guid.NewGuid(), Name = "Test Client", CreatedAt = now, UpdatedAt = now };
        db.Clients.Add(client);

        var dept = new Department { Id = Guid.NewGuid(), Name = "TI", CreatedAt = now, UpdatedAt = now };
        db.Departments.Add(dept);

        var profile = new WorkflowProfile
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            DepartmentId = dept.Id,
            Name = "Default",
            SlaHours = slaHours,
            FirstResponseSlaHours = frtHours,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.WorkflowProfiles.Add(profile);

        var state = new WorkflowState
        {
            Id = Guid.NewGuid(),
            Name = "Open",
            IsInitial = true,
            IsFinal = false,
            PausesSla = false,
            SortOrder = 1,
            CreatedAt = now
        };
        db.WorkflowStates.Add(state);

        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Title = "Test Ticket",
            Description = "Test",
            WorkflowStateId = state.Id,
            WorkflowProfileId = profile.Id,
            Priority = TicketPriority.Medium,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var ticketRepo = new TicketRepository(db, new NullAgentMessaging());
        var workflowRepo = new WorkflowRepository(db);
        var activityLogRepo = new TicketActivityLogRepository(db);
        var activityLogService = new ActivityLogService(activityLogRepo, NullLogger<ActivityLogService>.Instance);
        var calendarRepo = new SlaCalendarRepository(db);

        var slaService = new SlaService(
            new WorkflowProfileRepository(db),
            ticketRepo,
            activityLogService,
            calendarRepo,
            NullLogger<SlaService>.Instance);

        return new SlaTestFixture(db, ticket, slaService);
    }

    private sealed class SlaTestFixture : IAsyncDisposable
    {
        public SlaTestFixture(DiscoveryDbContext db, Ticket ticket, SlaService slaService)
        {
            Db = db;
            Ticket = ticket;
            SlaService = slaService;
        }

        public DiscoveryDbContext Db { get; }
        public Ticket Ticket { get; }
        public SlaService SlaService { get; }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }

    private sealed class SlaTestDiscoveryDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Ignore todas as entidades não necessárias para este teste
            var allowedTypes = new HashSet<Type>
            {
                typeof(Client),
                typeof(Department),
                typeof(WorkflowProfile),
                typeof(WorkflowState),
                typeof(Ticket),
                typeof(TicketActivityLog),
                typeof(SlaCalendar),
                typeof(SlaCalendarHoliday)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowedTypes.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(e => { e.HasKey(c => c.Id); e.Property(c => c.Name).IsRequired(); });
            modelBuilder.Entity<Department>(e => { e.HasKey(d => d.Id); e.Property(d => d.Name).IsRequired(); });
            modelBuilder.Entity<WorkflowProfile>(e => { e.HasKey(p => p.Id); });
            modelBuilder.Entity<WorkflowState>(e => { e.HasKey(s => s.Id); });
            modelBuilder.Entity<Ticket>(e =>
            {
                e.HasKey(t => t.Id);
                e.Ignore(t => t.DaysOpen);
            });
            modelBuilder.Entity<TicketActivityLog>(e => { e.HasKey(l => l.Id); });
            modelBuilder.Entity<SlaCalendar>(e => { e.HasKey(c => c.Id); });
            modelBuilder.Entity<SlaCalendarHoliday>(e => { e.HasKey(h => h.Id); });
        }
    }
}

file sealed class NullAgentMessaging : IAgentMessaging
{
    public bool IsConnected => false;
    public Task SendCommandAsync(Guid agentId, Guid commandId, string commandType, string payload) => Task.CompletedTask;
    public Task PublishDashboardEventAsync(DashboardEventMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SubscribeToAgentMessagesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task PublishP2pDiscoverySnapshotAsync(Guid clientId, Guid siteId, string payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
