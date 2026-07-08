using Discovery.Core.Entities.Identity;
using Discovery.Core.Interfaces.Identity;

namespace Discovery.Infrastructure.Services;

public sealed class UserGroupService : IUserGroupService
{
    private readonly IUserGroupRepository _repo;

    public UserGroupService(IUserGroupRepository repo) => _repo = repo;

    public async Task<IReadOnlyList<UserGroup>> GetAllAsync(CancellationToken ct = default)
    {
        var groups = await _repo.GetAllAsync();
        return groups.ToList().AsReadOnly();
    }

    public Task<UserGroup?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _repo.GetByIdAsync(id);

    public Task<UserGroup> CreateAsync(UserGroup group, CancellationToken ct = default)
    {
        group.Id = Guid.NewGuid();
        group.CreatedAt = DateTime.UtcNow;
        group.UpdatedAt = DateTime.UtcNow;
        return _repo.CreateAsync(group);
    }

    public async Task<UserGroup> UpdateAsync(UserGroup group, CancellationToken ct = default)
    {
        group.UpdatedAt = DateTime.UtcNow;
        return await _repo.UpdateAsync(group);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        => _repo.DeleteAsync(id);

    public Task AddMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default)
        => _repo.AddMemberAsync(groupId, userId);

    public Task RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default)
        => _repo.RemoveMemberAsync(groupId, userId);

    public Task<IEnumerable<Guid>> GetMemberIdsAsync(Guid groupId, CancellationToken ct = default)
        => _repo.GetMemberIdsAsync(groupId);
}
