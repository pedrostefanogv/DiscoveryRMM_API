using Discovery.Core.Entities.Identity;

namespace Discovery.Core.Interfaces.Identity;

public interface IUserGroupRepository
{
    Task<UserGroup?> GetByIdAsync(Guid id);
    Task<IEnumerable<UserGroup>> GetAllAsync();
    Task<UserGroup> CreateAsync(UserGroup group);
    Task<UserGroup> UpdateAsync(UserGroup group);
    Task<bool> DeleteAsync(Guid id);

    // Memberships
    Task AddMemberAsync(Guid groupId, Guid userId);
    Task RemoveMemberAsync(Guid groupId, Guid userId);
    Task<IEnumerable<Guid>> GetMemberIdsAsync(Guid groupId);

    /// <summary>Vínculos (com data de entrada) para compor a listagem de membros do grupo.</summary>
    Task<IEnumerable<UserGroupMembership>> GetMembershipsAsync(Guid groupId);

    /// <summary>Vínculo específico (para devolver a data de entrada real ao adicionar membro).</summary>
    Task<UserGroupMembership?> GetMembershipAsync(Guid groupId, Guid userId);
    Task<IEnumerable<Guid>> GetGroupIdsForUserAsync(Guid userId);

    // Role assignments
    Task<IEnumerable<UserGroupRole>> GetRolesForGroupAsync(Guid groupId);
    Task<IEnumerable<UserGroupRole>> GetRolesForUserAsync(Guid userId);

    /// <summary>
    /// Retorna todas as atribuicoes de role do usuario com as permissoes ja incluidas,
    /// em uma unica query. Elimina o N+1 tradicional.
    /// </summary>
    Task<IReadOnlyList<Discovery.Core.DTOs.Identity.RoleAssignmentWithPermissions>> GetRolesWithPermissionsForUserAsync(Guid userId);

    Task AssignRoleAsync(UserGroupRole assignment);
    Task RemoveRoleAssignmentAsync(Guid assignmentId);
}
