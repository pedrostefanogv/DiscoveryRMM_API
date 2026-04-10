using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class DepartmentRepository : IDepartmentRepository
{
    private readonly DiscoveryDbContext _db;

    public DepartmentRepository(DiscoveryDbContext db) => _db = db;

    public async Task<Department?> GetByIdAsync(Guid id)
    {
        return await _db.Set<Department>()
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id);
    }

    public async Task<List<Department>> GetGlobalAsync()
    {
        return await _db.Set<Department>()
            .AsNoTracking()
            .Where(d => d.ClientId == null)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .ToListAsync();
    }

    public async Task<List<Department>> GetByClientAsync(Guid clientId, bool includeGlobal = true)
    {
        IQueryable<Department> query = _db.Set<Department>().AsNoTracking();

        if (includeGlobal)
        {
            query = query.Where(d => d.ClientId == clientId || d.ClientId == null);
        }
        else
        {
            query = query.Where(d => d.ClientId == clientId);
        }

        return await query
            .OrderByDescending(d => d.ClientId != null) // Client-specific first
            .ThenBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .ToListAsync();
    }

    public async Task<List<Department>> GetByClientAsync(Guid clientId, bool includeGlobal = true, bool activeOnly = true)
    {
        var departments = await GetByClientAsync(clientId, includeGlobal);
        
        if (activeOnly)
        {
            departments = departments.Where(d => d.IsActive).ToList();
        }

        return departments;
    }

    public async Task<Department> CreateAsync(Department department)
    {
        department.Id = Guid.NewGuid();
        department.CreatedAt = DateTime.UtcNow;
        department.UpdatedAt = DateTime.UtcNow;

        _db.Set<Department>().Add(department);
        await _db.SaveChangesAsync();
        
        return department;
    }

    public async Task<Department> UpdateAsync(Department department)
    {
        department.UpdatedAt = DateTime.UtcNow;
        _db.Set<Department>().Update(department);
        await _db.SaveChangesAsync();
        
        return department;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var department = await GetByIdAsync(id);
        if (department == null)
            return false;

        // Soft delete
        department.IsActive = false;
        await UpdateAsync(department);
        
        return true;
    }

    public async Task<bool> ExistsByNameAsync(Guid? clientId, string name)
    {
        return await _db.Set<Department>()
            .AsNoTracking()
            .AnyAsync(d => (d.ClientId == clientId || (clientId == null && d.ClientId == null)) &&
                          EF.Functions.ILike(d.Name, name));
    }
}
