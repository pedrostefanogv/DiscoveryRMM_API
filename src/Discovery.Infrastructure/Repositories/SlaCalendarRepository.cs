using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class SlaCalendarRepository : ISlaCalendarRepository
{
    private readonly DiscoveryDbContext _db;

    public SlaCalendarRepository(DiscoveryDbContext db) => _db = db;

    public async Task<SlaCalendar?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.SlaCalendars
            .Include(c => c.Holidays)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<SlaCalendar>> GetAllAsync(Guid? clientId = null, CancellationToken ct = default)
    {
        var query = _db.SlaCalendars.Include(c => c.Holidays).AsNoTracking();
        if (clientId.HasValue)
            query = query.Where(c => c.ClientId == null || c.ClientId == clientId.Value);
        return await query.OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<SlaCalendar> CreateAsync(SlaCalendar calendar, CancellationToken ct = default)
    {
        calendar.Id = Guid.NewGuid();
        calendar.CreatedAt = DateTime.UtcNow;
        calendar.UpdatedAt = DateTime.UtcNow;
        _db.SlaCalendars.Add(calendar);
        await _db.SaveChangesAsync(ct);
        return calendar;
    }

    public async Task UpdateAsync(SlaCalendar calendar, CancellationToken ct = default)
    {
        calendar.UpdatedAt = DateTime.UtcNow;
        _db.SlaCalendars.Update(calendar);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var calendar = await _db.SlaCalendars.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (calendar is not null)
        {
            _db.SlaCalendars.Remove(calendar);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<SlaCalendarHoliday> AddHolidayAsync(SlaCalendarHoliday holiday, CancellationToken ct = default)
    {
        holiday.Id = Guid.NewGuid();
        _db.SlaCalendarHolidays.Add(holiday);
        await _db.SaveChangesAsync(ct);
        return holiday;
    }

    public async Task DeleteHolidayAsync(Guid holidayId, CancellationToken ct = default)
    {
        var h = await _db.SlaCalendarHolidays.FirstOrDefaultAsync(h => h.Id == holidayId, ct);
        if (h is not null)
        {
            _db.SlaCalendarHolidays.Remove(h);
            await _db.SaveChangesAsync(ct);
        }
    }
}
