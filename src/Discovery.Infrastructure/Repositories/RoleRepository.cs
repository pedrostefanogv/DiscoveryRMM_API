using Discovery.Core.Entities.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces.Identity;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class RoleRepository : IRoleRepository
{
    private readonly DiscoveryDbContext _db;
    public RoleRepository(DiscoveryDbContext db) => _db = db;

    public Task<Role?> GetByIdAsync(Guid id)
        => _db.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id);

    public async Task<IEnumerable<Role>> GetAllAsync()
        => await _db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync();

    public async Task<Role> CreateAsync(Role role)
    {
        if (role.Id == Guid.Empty) role.Id = IdGenerator.NewId();
        role.CreatedAt = DateTime.UtcNow;
        role.UpdatedAt = DateTime.UtcNow;
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();
        return role;
    }

    public async Task<Role> UpdateAsync(Role role)
    {
        role.UpdatedAt = DateTime.UtcNow;
        _db.Roles.Update(role);
        await _db.SaveChangesAsync();
        return role;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        // Roles de sistema não podem ser deletadas
        var role = await _db.Roles.FindAsync(id);
        if (role is null || role.IsSystem) return false;
        _db.Roles.Remove(role);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<IEnumerable<Permission>> GetPermissionsForRoleAsync(Guid roleId)
        => await _db.RolePermissions
            .Where(rp => rp.RoleId == roleId)
            .Join(_db.Permissions, rp => rp.PermissionId, p => p.Id, (rp, p) => p)
            .AsNoTracking()
            .ToListAsync();

    public async Task AddPermissionToRoleAsync(Guid roleId, Guid permissionId)
    {
        var exists = await _db.RolePermissions.AnyAsync(rp => rp.RoleId == roleId && rp.PermissionId == permissionId);
        if (exists) return;
        _db.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionId = permissionId });
        await _db.SaveChangesAsync();
    }

    public async Task RemovePermissionFromRoleAsync(Guid roleId, Guid permissionId)
    {
        await _db.RolePermissions
            .Where(rp => rp.RoleId == roleId && rp.PermissionId == permissionId)
            .ExecuteDeleteAsync();
    }

    public async Task<IEnumerable<Permission>> GetAllPermissionsAsync()
        => await _db.Permissions.AsNoTracking().OrderBy(p => p.ResourceType).ThenBy(p => p.ActionType).ToListAsync();

    public Task<Permission?> GetPermissionAsync(Guid id)
        => _db.Permissions.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id);
}
