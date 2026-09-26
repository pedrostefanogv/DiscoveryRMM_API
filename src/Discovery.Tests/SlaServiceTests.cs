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

    // ── Robustez de configuração (fuso/dias úteis inválidos) ─────────────

    [Test]
    public void AddWorkingHours_InvalidTimezone_FallsBackToUtcWithoutThrowing()
    {
        var calendar = BuildCalendar("Fuso/Inexistente");
        var from = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc); // segunda 09:00

        var result = SlaService.AddWorkingHours(from, 8, calendar);

        Assert.That(result, Is.EqualTo(new DateTime(2026, 6, 1, 17, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void AddWorkingHours_EmptyWorkDaysJson_FallsBackToWeekdays()
    {
        var calendar = BuildCalendar("UTC");
        calendar.WorkDaysJson = "[]"; // antes causava laço infinito em NextWorkDayStart

        var from = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

        var result = SlaService.AddWorkingHours(from, 8, calendar);

        Assert.That(result, Is.EqualTo(new DateTime(2026, 6, 1, 17, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void AddWorkingHours_MalformedWorkDaysJson_FallsBackToWeekdays()
    {
        var calendar = BuildCalendar("UTC");
        calendar.WorkDaysJson = "not-json";

        var from = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

        var result = SlaService.AddWorkingHours(from, 8, calendar);

        Assert.That(result, Is.EqualTo(new DateTime(2026, 6, 1, 17, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void AddWorkingHours_CustomWorkDays_IncludesSaturday()
    {
        var calendar = BuildCalendar("UTC");
        calendar.WorkDaysJson = "[1,2,3,4,5,6]"; // inclui sábado
        var saturday = new DateTime(2026, 6, 6, 9, 0, 0, DateTimeKind.Utc);

        var result = SlaService.AddWorkingHours(saturday, 8, calendar);

        Assert.That(result, Is.EqualTo(new DateTime(2026, 6, 6, 17, 0, 0, DateTimeKind.Utc)));
    }

    // ── Turnos que viram o dia e horário de verão ────────────────────────

    [Test]
    public void AddWorkingHours_OvernightShift_CrossesMidnight()
    {
        var calendar = BuildCalendar("UTC");
        calendar.WorkDayStartHour = 22; // 22:00 → 06:00 do dia seguinte
        calendar.WorkDayEndHour = 6;

        var from = new DateTime(2026, 6, 1, 23, 0, 0, DateTimeKind.Utc); // segunda 23:00

        var result = SlaService.AddWorkingHours(from, 4, calendar);

        Assert.That(result, Is.EqualTo(new DateTime(2026, 6, 2, 3, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void AddWorkingHours_OvernightShift_JumpsToNextShiftDay()
    {
        var calendar = BuildCalendar("UTC");
        calendar.WorkDayStartHour = 22;
        calendar.WorkDayEndHour = 6;

        // Terça 05:00 está no turno iniciado na segunda (termina 06:00 terça):
        // consome 1h até 06:00 e depois 1h no turno de terça 22:00 → 23:00.
        var from = new DateTime(2026, 6, 2, 5, 0, 0, DateTimeKind.Utc);

        var result = SlaService.AddWorkingHours(from, 2, calendar);

        Assert.That(result, Is.EqualTo(new DateTime(2026, 6, 2, 23, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void CountWorkingHours_OvernightShift()
    {
        var calendar = BuildCalendar("UTC");
        calendar.WorkDayStartHour = 22;
        calendar.WorkDayEndHour = 6;

        var hours = SlaService.CountWorkingHours(
            new DateTime(2026, 6, 1, 23, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 2, 3, 0, 0, DateTimeKind.Utc),
            calendar);

        Assert.That(hours, Is.EqualTo(4.0).Within(0.01));
    }

    [Test]
    public void CountWorkingHours_DaylightSavingSpringForward_UsesRealDuration()
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException) { Assert.Ignore("tzdata indisponível neste ambiente."); return; }
        catch (InvalidTimeZoneException) { Assert.Ignore("tzdata inválida neste ambiente."); return; }

        if (!tz.SupportsDaylightSavingTime)
        {
            Assert.Ignore("Fuso sem horário de verão neste ambiente.");
            return;
        }

        var calendar = BuildCalendar("America/New_York");
        calendar.WorkDayStartHour = 0;   // jornada contínua de 24h
        calendar.WorkDayEndHour = 24;
        calendar.WorkDaysJson = "[0,1,2,3,4,5,6]";

        // 08/03/2026 (domingo) é o início do horário de verão nos EUA: o relógio
        // pula 02:00→03:00, então o dia tem 23 horas reais.
        // 00:00 EST = 05:00 UTC; 00:00 EDT do dia seguinte = 04:00 UTC.
        var hours = SlaService.CountWorkingHours(
            new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 9, 4, 0, 0, DateTimeKind.Utc),
            calendar);

        Assert.That(hours, Is.EqualTo(23.0).Within(0.01));
    }

    // ── Contagem de horas úteis (base do percentual de SLA) ───────────────

    [Test]
    public void CountWorkingHours_WithinSameWorkday()
    {
        var calendar = BuildCalendar("UTC");

        var hours = SlaService.CountWorkingHours(
            new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            calendar);

        Assert.That(hours, Is.EqualTo(3.0).Within(0.01));
    }

    [Test]
    public void CountWorkingHours_AcrossWeekend_IgnoresNonWorkingDays()
    {
        var calendar = BuildCalendar("UTC"); // seg-sex 08-18

        // Sexta 17:00 → segunda 09:30 = 1h (sexta) + 1,5h (segunda)
        var hours = SlaService.CountWorkingHours(
            new DateTime(2026, 6, 5, 17, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 8, 9, 30, 0, DateTimeKind.Utc),
            calendar);

        Assert.That(hours, Is.EqualTo(2.5).Within(0.01));
    }

    [Test]
    public void CountWorkingHours_SkipsHoliday()
    {
        var calendar = BuildCalendar("UTC");
        calendar.Holidays.Add(new SlaCalendarHoliday
        {
            Id = Guid.NewGuid(),
            CalendarId = calendar.Id,
            Name = "Feriado",
            Date = new DateTime(2026, 6, 1),
            HolidayTypeValue = (int)HolidayType.Fixed
        });

        // 01/06 (segunda) é feriado; de segunda 09:00 a terça 12:00 sobram 4h
        // (terça 08:00-12:00).
        var hours = SlaService.CountWorkingHours(
            new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc),
            calendar);

        Assert.That(hours, Is.EqualTo(4.0).Within(0.01));
    }

    [Test]
    public async Task GetSlaStatusAsync_WithCalendar_MeasuresInBusinessHours()
    {
        var calendar = BuildCalendar("UTC");
        await using var fixture = await CreateFixtureAsync(slaHours: 8, calendar: calendar);

        var now = DateTime.UtcNow;
        var ticket = fixture.Ticket;
        ticket.CreatedAt = now;
        ticket.SlaExpiresAt = SlaService.AddWorkingHours(now, 8, calendar);
        fixture.Db.Tickets.Update(ticket);
        await fixture.Db.SaveChangesAsync();

        var (hoursRemaining, percentUsed, breached) = await fixture.SlaService.GetSlaStatusAsync(ticket.Id);

        // O total de 8h úteis equivale exatamente ao vencimento, independentemente
        // do horário em que o teste roda (medida em horas úteis, não em relógio).
        Assert.That(breached, Is.False);
        Assert.That(percentUsed, Is.LessThan(1.0));
        Assert.That(hoursRemaining, Is.InRange(7, 8));
    }

    [Test]
    public async Task GetSlaContextForTicketAsync_UsesProfileWarningThreshold()
    {
        var calendar = BuildCalendar("UTC");
        await using var fixture = await CreateFixtureAsync(calendar: calendar, warningPercent: 50);

        var context = await fixture.SlaService.GetSlaContextForTicketAsync(fixture.Ticket);

        Assert.That(context.WarningThresholdPercent, Is.EqualTo(50));
        Assert.That(context.Calendar, Is.Not.Null);
    }

    [Test]
    public async Task GetSlaContextForTicketAsync_WithoutProfile_UsesDefaultThreshold()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.Ticket.WorkflowProfileId = null;

        var context = await fixture.SlaService.GetSlaContextForTicketAsync(fixture.Ticket);

        Assert.That(context.WarningThresholdPercent, Is.EqualTo(ISlaService.DefaultWarningThresholdPercent));
        Assert.That(context.Calendar, Is.Null);
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

    private static async Task<SlaTestFixture> CreateFixtureAsync(int slaHours = 8, int frtHours = 4, SlaCalendar? calendar = null, int? warningPercent = null)
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
            SlaCalendarId = calendar?.Id,
            SlaWarningPercent = warningPercent,
            FirstResponseSlaHours = frtHours,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.WorkflowProfiles.Add(profile);

        if (calendar is not null)
            db.SlaCalendars.Add(calendar);

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
    public Task PublishSiteFanoutCommandAsync(Guid clientId, Guid siteId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishClientFanoutCommandAsync(Guid clientId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishGlobalFanoutCommandAsync(CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishDashboardEventAsync(DashboardEventMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, Guid overrideClientId, Guid overrideSiteId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishRemoteDebugControlAsync(Guid clientId, Guid siteId, Guid agentId, string payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendCommandToSubjectAsync(Guid clientId, Guid siteId, Guid agentId, Guid commandId, string commandType, string payload) => Task.CompletedTask;
    public Task SubscribeToAgentMessagesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
