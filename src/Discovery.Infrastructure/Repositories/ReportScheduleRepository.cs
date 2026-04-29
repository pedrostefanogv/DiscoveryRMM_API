using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class ReportScheduleRepository : IReportScheduleRepository
{
    private readonly DiscoveryDbContext _db;

    public ReportScheduleRepository(DiscoveryDbContext db) => _db = db;

    public async Task<ReportSchedule> CreateAsync(ReportSchedule schedule)
    {
        schedule.Id = IdGenerator.NewId();
        schedule.CreatedAt = DateTime.UtcNow;
        schedule.UpdatedAt = DateTime.UtcNow;

        _db.ReportSchedules.Add(schedule);
        await _db.SaveChangesAsync();

        return schedule;
    }

    public async Task<ReportSchedule?> GetByIdAsync(Guid id, Guid? clientId = null)
    {
        return await _db.ReportSchedules
            .AsNoTracking()
            .Where(s => s.Id == id)
            .Where(s => !clientId.HasValue || s.ClientId == clientId.Value)
            .FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<ReportSchedule>> GetByTemplateAsync(Guid templateId, Guid? clientId = null)
    {
        return await _db.ReportSchedules
            .AsNoTracking()
            .Where(s => s.TemplateId == templateId)
            .Where(s => !clientId.HasValue || s.ClientId == clientId.Value)
            .OrderBy(s => s.ScheduleLabel)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<ReportSchedule>> GetAllAsync(Guid? clientId = null, bool? isActive = true)
    {
        var query = _db.ReportSchedules.AsNoTracking();

        if (clientId.HasValue)
            query = query.Where(s => s.ClientId == clientId.Value);
        if (isActive.HasValue)
            query = query.Where(s => s.IsActive == isActive.Value);

        return await query
            .OrderBy(s => s.ScheduleLabel)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<ReportSchedule>> GetDueSchedulesAsync(DateTime utcNow, int limit = 50)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);

        return await _db.ReportSchedules
            .AsNoTracking()
            .Where(s => s.IsActive)
            .Where(s => s.NextTriggerAt == null || s.NextTriggerAt <= utcNow)
            .OrderBy(s => s.NextTriggerAt)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task UpdateAsync(ReportSchedule schedule)
    {
        schedule.UpdatedAt = DateTime.UtcNow;
        _db.ReportSchedules.Update(schedule);
        await _db.SaveChangesAsync();
    }

    public async Task<bool> DeleteAsync(Guid id, Guid? clientId = null)
    {
        var deleted = await _db.ReportSchedules
            .Where(s => s.Id == id)
            .Where(s => !clientId.HasValue || s.ClientId == clientId.Value)
            .ExecuteDeleteAsync();

        return deleted > 0;
    }
}
