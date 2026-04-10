using Discovery.Core.Entities.Identity;

namespace Discovery.Core.Interfaces.Identity;

public interface IRoleRepository
{
    Task<Role?> GetByIdAsync(Guid id);
    Task<IEnumerable<Role>> GetAllAsync();
    Task<Role> CreateAsync(Role role);
    Task<Role> UpdateAsync(Role role);
    Task<bool> DeleteAsync(Guid id);

    // Permissions
    Task<IEnumerable<Permission>> GetPermissionsForRoleAsync(Guid roleId);
    Task AddPermissionToRoleAsync(Guid roleId, Guid permissionId);
    Task RemovePermissionFromRoleAsync(Guid roleId, Guid permissionId);

    // All permissions
    Task<IEnumerable<Permission>> GetAllPermissionsAsync();
    Task<Permission?> GetPermissionAsync(Guid id);
}
