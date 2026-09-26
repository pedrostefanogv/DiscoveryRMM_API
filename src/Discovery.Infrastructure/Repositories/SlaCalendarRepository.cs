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
        // Sem Include(Holidays): a listagem usa GetHolidayCountsAsync para o número.
        var query = _db.SlaCalendars.AsNoTracking();
        if (clientId.HasValue)
            query = query.Where(c => c.ClientId == null || c.ClientId == clientId.Value);
        return await query.OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetHolidayCountsAsync(Guid? clientId = null, CancellationToken ct = default)
    {
        var query = _db.SlaCalendarHolidays.AsNoTracking();

        if (clientId.HasValue)
        {
            query = query.Where(h => _db.SlaCalendars
                .Any(c => c.Id == h.CalendarId && (c.ClientId == null || c.ClientId == clientId.Value)));
        }

        var counts = await query
            .GroupBy(h => h.CalendarId)
            .Select(group => new { CalendarId = group.Key, Count = group.Count() })
            .ToListAsync(ct);

        return counts.ToDictionary(item => item.CalendarId, item => item.Count);
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
        // Marca apenas a linha do calendário como modificada. O Update() do DbSet
        // percorre o grafo e marcaria todos os feriados como modificados também.
        _db.Entry(calendar).State = EntityState.Modified;
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

    public async Task ClearDefaultFlagAsync(Guid? clientId, Guid exceptId, CancellationToken ct = default)
    {
        // Apenas um padrão por escopo: global ou de um cliente específico.
        var query = _db.SlaCalendars.Where(c => c.IsDefault && c.Id != exceptId);
        query = clientId.HasValue
            ? query.Where(c => c.ClientId == clientId.Value)
            : query.Where(c => c.ClientId == null);

        var others = await query.ToListAsync(ct);
        if (others.Count == 0) return;

        foreach (var other in others)
            other.IsDefault = false;

        await _db.SaveChangesAsync(ct);
    }

    public async Task<SlaCalendarHoliday> AddHolidayAsync(SlaCalendarHoliday holiday, CancellationToken ct = default)
    {
        holiday.Id = Guid.NewGuid();
        _db.SlaCalendarHolidays.Add(holiday);
        await _db.SaveChangesAsync(ct);
        return holiday;
    }

    public async Task UpdateHolidayAsync(SlaCalendarHoliday holiday, CancellationToken ct = default)
    {
        // Data é opcional (feriado relativo). Só normaliza quando existe.
        if (holiday.Date is { Kind: DateTimeKind.Utc } utcDate)
            holiday.Date = DateTime.SpecifyKind(utcDate, DateTimeKind.Unspecified);
        // Idem: não percorrer o grafo (o feriado carrega a navegação Calendar, e o
        // Update() acabaria marcando o calendário inteiro como modificado).
        _db.Entry(holiday).State = EntityState.Modified;
        await _db.SaveChangesAsync(ct);
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
