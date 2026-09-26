using Discovery.Core.Entities;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public sealed class SlaCalendarService : ISlaCalendarService
{
    private readonly ISlaCalendarRepository _repo;
    public SlaCalendarService(ISlaCalendarRepository repo) => _repo = repo;

    public Task<SlaCalendar?> GetByIdAsync(Guid id, CancellationToken ct = default) => _repo.GetByIdAsync(id, ct);
    public Task<IReadOnlyList<SlaCalendar>> GetAllAsync(Guid? clientId = null, CancellationToken ct = default) => _repo.GetAllAsync(clientId, ct);
    public Task<IReadOnlyDictionary<Guid, int>> GetHolidayCountsAsync(Guid? clientId = null, CancellationToken ct = default) => _repo.GetHolidayCountsAsync(clientId, ct);
    public Task<SlaCalendar> CreateAsync(SlaCalendar calendar, CancellationToken ct = default) => _repo.CreateAsync(calendar, ct);
    public Task UpdateAsync(SlaCalendar calendar, CancellationToken ct = default) => _repo.UpdateAsync(calendar, ct);
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => _repo.DeleteAsync(id, ct);

    public Task ClearDefaultFlagAsync(Guid? clientId, Guid exceptId, CancellationToken ct = default) => _repo.ClearDefaultFlagAsync(clientId, exceptId, ct);
    public Task<SlaCalendarHoliday> AddHolidayAsync(SlaCalendarHoliday holiday, CancellationToken ct = default) => _repo.AddHolidayAsync(holiday, ct);
    public Task UpdateHolidayAsync(SlaCalendarHoliday holiday, CancellationToken ct = default) => _repo.UpdateHolidayAsync(holiday, ct);
    public Task DeleteHolidayAsync(Guid holidayId, CancellationToken ct = default) => _repo.DeleteHolidayAsync(holidayId, ct);
}
