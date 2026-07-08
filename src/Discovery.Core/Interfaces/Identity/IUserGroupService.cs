using Discovery.Core.Entities.Identity;

namespace Discovery.Core.Interfaces.Identity;

public interface IUserGroupService
{
    Task<IReadOnlyList<UserGroup>> GetAllAsync(CancellationToken ct = default);
    Task<UserGroup?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<UserGroup> CreateAsync(UserGroup group, CancellationToken ct = default);
    Task<UserGroup> UpdateAsync(UserGroup group, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task AddMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default);
    Task RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default);
    Task<IEnumerable<Guid>> GetMemberIdsAsync(Guid groupId, CancellationToken ct = default);
}
