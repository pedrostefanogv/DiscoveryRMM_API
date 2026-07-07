using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class ReportFilterPresetRepository : IReportFilterPresetRepository
{
    private readonly DiscoveryDbContext _db;

    public ReportFilterPresetRepository(DiscoveryDbContext db)
    {
        _db = db;
    }

    public async Task<ReportFilterPreset> CreateAsync(ReportFilterPreset preset)
    {
        preset.Id = Guid.NewGuid();
        preset.CreatedAt = DateTime.UtcNow;
        preset.UpdatedAt = DateTime.UtcNow;
        _db.Set<ReportFilterPreset>().Add(preset);
        await _db.SaveChangesAsync();
        return preset;
    }

    public async Task<IReadOnlyList<ReportFilterPreset>> GetByUserAsync(Guid userId, Guid templateId)
    {
        return await _db.Set<ReportFilterPreset>()
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.TemplateId == templateId)
            .OrderBy(x => x.Name)
            .ToListAsync();
    }

    public async Task<ReportFilterPreset?> GetByIdAsync(Guid id, Guid userId)
    {
        return await _db.Set<ReportFilterPreset>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
    }

    public async Task<ReportFilterPreset?> UpdateAsync(ReportFilterPreset preset)
    {
        var existing = await _db.Set<ReportFilterPreset>()
            .FirstOrDefaultAsync(x => x.Id == preset.Id && x.UserId == preset.UserId);
        if (existing is null) return null;

        existing.Name = preset.Name;
        existing.FiltersJson = preset.FiltersJson;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id, Guid userId)
    {
        var existing = await _db.Set<ReportFilterPreset>()
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (existing is null) return false;

        _db.Set<ReportFilterPreset>().Remove(existing);
        await _db.SaveChangesAsync();
        return true;
    }
}
