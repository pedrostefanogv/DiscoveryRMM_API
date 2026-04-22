using Discovery.Core.Entities.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces.Identity;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class UserGroupRepository : IUserGroupRepository
{
    private readonly DiscoveryDbContext _db;
    public UserGroupRepository(DiscoveryDbContext db) => _db = db;

    public Task<UserGroup?> GetByIdAsync(Guid id)
        => _db.UserGroups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id);

    public async Task<IEnumerable<UserGroup>> GetAllAsync()
        => await _db.UserGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();

    public async Task<UserGroup> CreateAsync(UserGroup group)
    {
        if (group.Id == Guid.Empty) group.Id = IdGenerator.NewId();
        group.CreatedAt = DateTime.UtcNow;
        group.UpdatedAt = DateTime.UtcNow;
        _db.UserGroups.Add(group);
        await _db.SaveChangesAsync();
        return group;
    }

    public async Task<UserGroup> UpdateAsync(UserGroup group)
    {
        group.UpdatedAt = DateTime.UtcNow;
        _db.UserGroups.Update(group);
        await _db.SaveChangesAsync();
        return group;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var rows = await _db.UserGroups.Where(g => g.Id == id).ExecuteDeleteAsync();
        return rows > 0;
    }

    public async Task AddMemberAsync(Guid groupId, Guid userId)
    {
        var exists = await _db.UserGroupMemberships
            .AnyAsync(m => m.GroupId == groupId && m.UserId == userId);
        if (exists) return;

        _db.UserGroupMemberships.Add(new UserGroupMembership
        {
            GroupId = groupId,
            UserId = userId,
            JoinedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    public async Task RemoveMemberAsync(Guid groupId, Guid userId)
    {
        await _db.UserGroupMemberships
            .Where(m => m.GroupId == groupId && m.UserId == userId)
            .ExecuteDeleteAsync();
    }

    public async Task<IEnumerable<Guid>> GetMemberIdsAsync(Guid groupId)
        => await _db.UserGroupMemberships
            .Where(m => m.GroupId == groupId)
            .Select(m => m.UserId)
            .ToListAsync();

    public async Task<IEnumerable<Guid>> GetGroupIdsForUserAsync(Guid userId)
        => await _db.UserGroupMemberships
            .Where(m => m.UserId == userId)
            .Select(m => m.GroupId)
            .ToListAsync();

    public async Task<IEnumerable<UserGroupRole>> GetRolesForGroupAsync(Guid groupId)
        => await _db.UserGroupRoles.AsNoTracking()
            .Where(r => r.GroupId == groupId)
            .ToListAsync();

    public async Task<IEnumerable<UserGroupRole>> GetRolesForUserAsync(Guid userId)
        => await _db.UserGroupMemberships
            .Where(m => m.UserId == userId)
            .Join(_db.UserGroupRoles, m => m.GroupId, r => r.GroupId, (m, r) => r)
            .AsNoTracking()
            .ToListAsync();

    public async Task AssignRoleAsync(UserGroupRole assignment)
    {
        if (assignment.Id == Guid.Empty) assignment.Id = IdGenerator.NewId();
        assignment.AssignedAt = DateTime.UtcNow;
        _db.UserGroupRoles.Add(assignment);
        await _db.SaveChangesAsync();
    }

    public async Task RemoveRoleAssignmentAsync(Guid assignmentId)
    {
        await _db.UserGroupRoles.Where(r => r.Id == assignmentId).ExecuteDeleteAsync();
    }
}
