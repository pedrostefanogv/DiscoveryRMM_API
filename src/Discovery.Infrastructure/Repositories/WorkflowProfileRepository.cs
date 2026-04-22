using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class WorkflowProfileRepository : IWorkflowProfileRepository
{
    private readonly DiscoveryDbContext _db;

    public WorkflowProfileRepository(DiscoveryDbContext db) => _db = db;

    public async Task<WorkflowProfile?> GetByIdAsync(Guid id)
    {
        return await _db.Set<WorkflowProfile>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<List<WorkflowProfile>> GetGlobalAsync()
    {
        return await _db.Set<WorkflowProfile>()
            .AsNoTracking()
            .Where(p => p.ClientId == null && p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    public async Task<List<WorkflowProfile>> GetByClientAsync(Guid? clientId, bool includeGlobal = true)
    {
        IQueryable<WorkflowProfile> query = _db.Set<WorkflowProfile>()
            .AsNoTracking()
            .Where(p => p.IsActive);

        if (includeGlobal)
        {
            query = query.Where(p => p.ClientId == clientId || p.ClientId == null);
        }
        else
        {
            query = query.Where(p => p.ClientId == clientId);
        }

        return await query
            .OrderByDescending(p => p.ClientId != null) // Client-specific first
            .ThenBy(p => p.Name)
            .ToListAsync();
    }

    public async Task<List<WorkflowProfile>> GetByDepartmentAsync(Guid departmentId)
    {
        return await _db.Set<WorkflowProfile>()
            .AsNoTracking()
            .Where(p => p.DepartmentId == departmentId && p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    public async Task<WorkflowProfile?> GetDefaultByDepartmentAsync(Guid departmentId)
    {
        return await _db.Set<WorkflowProfile>()
            .AsNoTracking()
            .Where(p => p.DepartmentId == departmentId && p.IsActive)
            .OrderBy(p => p.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<WorkflowProfile> CreateAsync(WorkflowProfile profile)
    {
        profile.Id = Guid.NewGuid();
        profile.CreatedAt = DateTime.UtcNow;
        profile.UpdatedAt = DateTime.UtcNow;

        _db.Set<WorkflowProfile>().Add(profile);
        await _db.SaveChangesAsync();
        
        return profile;
    }

    public async Task<WorkflowProfile> UpdateAsync(WorkflowProfile profile)
    {
        profile.UpdatedAt = DateTime.UtcNow;
        _db.Set<WorkflowProfile>().Update(profile);
        await _db.SaveChangesAsync();
        
        return profile;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var profile = await GetByIdAsync(id);
        if (profile == null)
            return false;

        // Soft delete
        profile.IsActive = false;
        await UpdateAsync(profile);
        
        return true;
    }
}
