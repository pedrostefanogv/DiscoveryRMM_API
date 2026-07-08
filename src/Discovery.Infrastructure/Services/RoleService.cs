using Discovery.Core.Entities.Identity;
using Discovery.Core.Interfaces.Identity;

namespace Discovery.Infrastructure.Services;

public sealed class RoleService : IRoleService
{
    private readonly IRoleRepository _repo;

    public RoleService(IRoleRepository repo) => _repo = repo;

    public async Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken ct = default)
    {
        var roles = await _repo.GetAllAsync();
        return roles.ToList().AsReadOnly();
    }

    public Task<Role?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _repo.GetByIdAsync(id);

    public Task<Role> CreateAsync(Role role, CancellationToken ct = default)
    {
        role.Id = Guid.NewGuid();
        role.CreatedAt = DateTime.UtcNow;
        return _repo.CreateAsync(role);
    }

    public Task<Role> UpdateAsync(Role role, CancellationToken ct = default)
        => _repo.UpdateAsync(role);

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        => _repo.DeleteAsync(id);

    public Task<IEnumerable<Permission>> GetPermissionsAsync(Guid roleId, CancellationToken ct = default)
        => _repo.GetPermissionsForRoleAsync(roleId);

    public Task AddPermissionAsync(Guid roleId, Guid permissionId, CancellationToken ct = default)
        => _repo.AddPermissionToRoleAsync(roleId, permissionId);

    public Task RemovePermissionAsync(Guid roleId, Guid permissionId, CancellationToken ct = default)
        => _repo.RemovePermissionFromRoleAsync(roleId, permissionId);

    public Task<IEnumerable<Permission>> GetAllPermissionsAsync(CancellationToken ct = default)
        => _repo.GetAllPermissionsAsync();
}
