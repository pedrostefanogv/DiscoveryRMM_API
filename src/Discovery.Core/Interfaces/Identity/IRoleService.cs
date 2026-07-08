using Discovery.Core.Entities.Identity;

namespace Discovery.Core.Interfaces.Identity;

public interface IRoleService
{
    Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken ct = default);
    Task<Role?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Role> CreateAsync(Role role, CancellationToken ct = default);
    Task<Role> UpdateAsync(Role role, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<IEnumerable<Permission>> GetPermissionsAsync(Guid roleId, CancellationToken ct = default);
    Task AddPermissionAsync(Guid roleId, Guid permissionId, CancellationToken ct = default);
    Task RemovePermissionAsync(Guid roleId, Guid permissionId, CancellationToken ct = default);
    Task<IEnumerable<Permission>> GetAllPermissionsAsync(CancellationToken ct = default);
}
