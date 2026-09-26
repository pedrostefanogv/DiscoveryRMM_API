using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.SlaCalendars.Commands;
using Discovery.Core.Cqrs.SlaCalendars.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.SlaCalendars;

namespace Discovery.Tests;

/// <summary>
/// Testes dos comandos/consultas de feriado de calendário de SLA: validação,
/// duplicidade, mapeamento dos DTOs e guardas de exclusão.
/// </summary>
public class SlaCalendarHolidayTests
{
    [Test]
    public async Task AddHoliday_PersistsAndReturnsMappedDto()
    {
        var calendar = BuildCalendar();
        var handler = new AddSlaCalendarHolidayCommandHandler(new FakeSlaCalendarService(calendar), new FakeConfigurationAuditService());

        var result = await handler.Handle(
            new AddSlaCalendarHolidayCommand(calendar.Id, "Natal", new DateTime(2026, 12, 25), (int)HolidayType.Yearly, null, null, null, null),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Name, Is.EqualTo("Natal"));
        Assert.That(result.Value.HolidayType, Is.EqualTo((int)HolidayType.Yearly));
        Assert.That(calendar.Holidays.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task AddHoliday_WithoutName_ReturnsValidationError()
    {
        var calendar = BuildCalendar();
        var handler = new AddSlaCalendarHolidayCommandHandler(new FakeSlaCalendarService(calendar), new FakeConfigurationAuditService());

        var result = await handler.Handle(
            new AddSlaCalendarHolidayCommand(calendar.Id, "  ", new DateTime(2026, 12, 25), (int)HolidayType.Fixed, null, null, null, null),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(result.Errors[0].Field, Is.EqualTo("name"));
        Assert.That(calendar.Holidays, Is.Empty);
    }

    [Test]
    public async Task AddHoliday_RelativeWithoutMonthAndOccurrence_ReturnsValidationErrors()
    {
        var calendar = BuildCalendar();
        var handler = new AddSlaCalendarHolidayCommandHandler(new FakeSlaCalendarService(calendar), new FakeConfigurationAuditService());

        // Relative válido precisa de mês/ocorrência e, no método dds, do dia da semana.
        var result = await handler.Handle(
            new AddSlaCalendarHolidayCommand(calendar.Id, "Corpus Christi", new DateTime(2000, 1, 1), (int)HolidayType.Relative, null, null, null, (int)RelativeHolidayMethod.DayOfWeekOccurrence),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        var fields = result.Errors.Select(e => e.Field).ToList();
        Assert.That(fields, Does.Contain("relativeMonth"));
        Assert.That(fields, Does.Contain("relativeOccurrence"));
        Assert.That(fields, Does.Contain("relativeDayOfWeek"));
    }

    [Test]
    public async Task AddHoliday_DuplicateRule_ReturnsConflict()
    {
        var calendar = BuildCalendar();
        var svc = new FakeSlaCalendarService(calendar);
        var handler = new AddSlaCalendarHolidayCommandHandler(svc, new FakeConfigurationAuditService());
        var command = new AddSlaCalendarHolidayCommand(calendar.Id, "Natal", new DateTime(2026, 12, 25), (int)HolidayType.Yearly, null, null, null, null);

        var first = await handler.Handle(command, CancellationToken.None);
        var second = await handler.Handle(command with { Date = new DateTime(2027, 12, 25) }, CancellationToken.None);

        Assert.That(first.IsSuccess, Is.True);
        Assert.That(second.IsFailure, Is.True);
        Assert.That(second.Errors[0].Code, Is.EqualTo("Conflict"));
        Assert.That(calendar.Holidays.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task AddHoliday_UnknownCalendar_ReturnsNotFound()
    {
        var calendar = BuildCalendar();
        var handler = new AddSlaCalendarHolidayCommandHandler(new FakeSlaCalendarService(calendar), new FakeConfigurationAuditService());

        var result = await handler.Handle(
            new AddSlaCalendarHolidayCommand(Guid.NewGuid(), "Natal", new DateTime(2026, 12, 25), (int)HolidayType.Fixed, null, null, null, null),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    [Test]
    public async Task DeleteHoliday_Missing_ReturnsNotFound()
    {
        var calendar = BuildCalendar();
        var handler = new DeleteSlaCalendarHolidayCommandHandler(new FakeSlaCalendarService(calendar), new FakeConfigurationAuditService());

        var result = await handler.Handle(
            new DeleteSlaCalendarHolidayCommand(calendar.Id, Guid.NewGuid()),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    [Test]
    public async Task DeleteHoliday_Existing_RemovesFromCalendar()
    {
        var calendar = BuildCalendar();
        var svc = new FakeSlaCalendarService(calendar);
        var addHandler = new AddSlaCalendarHolidayCommandHandler(svc, new FakeConfigurationAuditService());
        var created = await addHandler.Handle(
            new AddSlaCalendarHolidayCommand(calendar.Id, "Natal", new DateTime(2026, 12, 25), (int)HolidayType.Yearly, null, null, null, null),
            CancellationToken.None);

        var handler = new DeleteSlaCalendarHolidayCommandHandler(svc, new FakeConfigurationAuditService());
        var result = await handler.Handle(new DeleteSlaCalendarHolidayCommand(calendar.Id, created.Value!.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(calendar.Holidays, Is.Empty);
    }

    [Test]
    public async Task ListCalendars_ExposesHolidayCount()
    {
        var calendar = BuildCalendar();
        calendar.Holidays.Add(new SlaCalendarHoliday { Id = Guid.NewGuid(), CalendarId = calendar.Id, Name = "Natal", Date = new DateTime(2026, 12, 25), HolidayTypeValue = (int)HolidayType.Yearly });
        calendar.Holidays.Add(new SlaCalendarHoliday { Id = Guid.NewGuid(), CalendarId = calendar.Id, Name = "Ano Novo", Date = new DateTime(2026, 1, 1), HolidayTypeValue = (int)HolidayType.Yearly });
        var handler = new ListSlaCalendarsQueryHandler(new FakeSlaCalendarService(calendar));

        var result = await handler.Handle(new ListSlaCalendarsQuery(null), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value![0].HolidayCount, Is.EqualTo(2));
    }

    [Test]
    public async Task GetCalendarById_ExposesHolidays()
    {
        var calendar = BuildCalendar();
        var holiday = new SlaCalendarHoliday { Id = Guid.NewGuid(), CalendarId = calendar.Id, Name = "Natal", Date = new DateTime(2026, 12, 25), HolidayTypeValue = (int)HolidayType.Yearly };
        calendar.Holidays.Add(holiday);
        var handler = new GetSlaCalendarByIdQueryHandler(new FakeSlaCalendarService(calendar));

        var result = await handler.Handle(new GetSlaCalendarByIdQuery(calendar.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Holidays, Has.Count.EqualTo(1));
        Assert.That(result.Value.Holidays[0].Name, Is.EqualTo("Natal"));
    }

    [Test]
    public async Task DeleteCalendar_InUseByWorkflowProfile_ReturnsConflict()
    {
        var calendar = BuildCalendar();
        var handler = new DeleteSlaCalendarCommandHandler(new FakeSlaCalendarService(calendar), new FakeWorkflowProfileRepository(inUseCount: 2), new FakeConfigurationAuditService());

        var result = await handler.Handle(new DeleteSlaCalendarCommand(calendar.Id), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Conflict"));
    }

    [Test]
    public async Task DeleteCalendar_NotInUse_Succeeds()
    {
        var calendar = BuildCalendar();
        var handler = new DeleteSlaCalendarCommandHandler(new FakeSlaCalendarService(calendar), new FakeWorkflowProfileRepository(inUseCount: 0), new FakeConfigurationAuditService());

        var result = await handler.Handle(new DeleteSlaCalendarCommand(calendar.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static SlaCalendar BuildCalendar() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Horário comercial",
        Timezone = "America/Sao_Paulo",
        WorkDayStartHour = 8,
        WorkDayEndHour = 18,
        WorkDaysJson = "[1,2,3,4,5]",
        Holidays = new List<SlaCalendarHoliday>(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private sealed class FakeSlaCalendarService(SlaCalendar calendar) : ISlaCalendarService
    {
        public Task<SlaCalendar?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<SlaCalendar?>(calendar.Id == id ? calendar : null);

        public Task<IReadOnlyList<SlaCalendar>> GetAllAsync(Guid? clientId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SlaCalendar>>(new[] { calendar });

        public Task<IReadOnlyDictionary<Guid, int>> GetHolidayCountsAsync(Guid? clientId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, int>>(
                new Dictionary<Guid, int> { [calendar.Id] = calendar.Holidays.Count });

        public Task<SlaCalendar> CreateAsync(SlaCalendar item, CancellationToken ct = default) => Task.FromResult(item);
        public Task UpdateAsync(SlaCalendar item, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task ClearDefaultFlagAsync(Guid? clientId, Guid exceptId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<SlaCalendarHoliday> AddHolidayAsync(SlaCalendarHoliday holiday, CancellationToken ct = default)
        {
            holiday.Id = Guid.NewGuid();
            calendar.Holidays.Add(holiday);
            return Task.FromResult(holiday);
        }

        public Task UpdateHolidayAsync(SlaCalendarHoliday holiday, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteHolidayAsync(Guid holidayId, CancellationToken ct = default)
        {
            var existing = calendar.Holidays.FirstOrDefault(h => h.Id == holidayId);
            if (existing is not null) calendar.Holidays.Remove(existing);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeConfigurationAuditService : IConfigurationAuditService
    {
        public List<(string EntityType, Guid EntityId, string FieldName, string? NewValue)> Changes { get; } = [];

        public Task LogChangeAsync(string entityType, Guid entityId, string fieldName,
            string? oldValue, string? newValue, string? reason = null, string? changedBy = null, string? ipAddress = null)
        {
            Changes.Add((entityType, entityId, fieldName, newValue));
            return Task.CompletedTask;
        }

        public Task<IEnumerable<ConfigurationAudit>> GetEntityHistoryAsync(string entityType, Guid entityId, int limit = 100)
            => Task.FromResult(Enumerable.Empty<ConfigurationAudit>());
        public Task<IEnumerable<ConfigurationAudit>> GetRecentChangesAsync(int days = 90, int limit = 1000)
            => Task.FromResult(Enumerable.Empty<ConfigurationAudit>());
        public Task<IEnumerable<ConfigurationAudit>> GetChangesByUserAsync(string username, int limit = 100)
            => Task.FromResult(Enumerable.Empty<ConfigurationAudit>());
        public Task<IEnumerable<ConfigurationAudit>> GetFieldHistoryAsync(string entityType, Guid entityId, string fieldName)
            => Task.FromResult(Enumerable.Empty<ConfigurationAudit>());
        public Task<IEnumerable<ConfigurationAudit>> GetAuditReportAsync(DateTime startDate, DateTime endDate)
            => Task.FromResult(Enumerable.Empty<ConfigurationAudit>());
    }

    private sealed class FakeWorkflowProfileRepository(int inUseCount) : IWorkflowProfileRepository
    {
        public Task<int> CountBySlaCalendarIdAsync(Guid slaCalendarId) => Task.FromResult(inUseCount);
        public Task<WorkflowProfile?> GetByIdAsync(Guid id) => Task.FromResult<WorkflowProfile?>(null);
        public Task<List<WorkflowProfile>> GetGlobalAsync() => Task.FromResult(new List<WorkflowProfile>());
        public Task<List<WorkflowProfile>> GetByClientAsync(Guid? clientId, bool includeGlobal = true) => Task.FromResult(new List<WorkflowProfile>());
        public Task<List<WorkflowProfile>> GetByDepartmentAsync(Guid departmentId) => Task.FromResult(new List<WorkflowProfile>());
        public Task<WorkflowProfile?> GetDefaultByDepartmentAsync(Guid departmentId) => Task.FromResult<WorkflowProfile?>(null);
        public Task<WorkflowProfile> CreateAsync(WorkflowProfile profile) => Task.FromResult(profile);
        public Task<WorkflowProfile> UpdateAsync(WorkflowProfile profile) => Task.FromResult(profile);
        public Task<bool> DeleteAsync(Guid id) => Task.FromResult(false);
    }
}
