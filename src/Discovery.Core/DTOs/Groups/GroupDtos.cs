using Discovery.Core.Enums.Identity;

namespace Discovery.Core.DTOs.Groups;

public class CreateGroupDto
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class UpdateGroupDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool? IsActive { get; set; }
}

public class GroupDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public int MemberCount { get; set; }
    public IEnumerable<GroupRoleAssignmentDto> RoleAssignments { get; set; } = [];
}

public class GroupRoleAssignmentDto
{
    public Guid AssignmentId { get; set; }
    public Guid RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public ScopeLevel ScopeLevel { get; set; }
    public Guid? ScopeId { get; set; }
    /// <summary>Nome do Client ou Site referenciado (para exibição).</summary>
    public string? ScopeName { get; set; }
}

public class AssignRoleToGroupDto
{
    public Guid RoleId { get; set; }
    public ScopeLevel ScopeLevel { get; set; } = ScopeLevel.Global;
    public Guid? ScopeId { get; set; }
}

public class AddGroupMemberDto
{
    public Guid UserId { get; set; }
}

/// <summary>
/// Membro de um grupo com dados de exibição. <see cref="AssignmentId"/> é o identificador
/// estável do vínculo usado como chave na UI (para membership a PK é (user_id, group_id),
/// então expomos o user_id).
/// </summary>
public class GroupMemberDto
{
    public string AssignmentId { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string? Login { get; set; }
    public string? Email { get; set; }
    public string? FullName { get; set; }
    public bool? IsActive { get; set; }
    public DateTime? AddedAt { get; set; }
}

/// <summary>Corpo de POST /api/v1/user-groups/{id}/roles.</summary>
public class AddGroupRoleDto
{
    public Guid RoleId { get; set; }
    public ScopeLevel ScopeLevel { get; set; } = ScopeLevel.Global;
    public Guid? ScopeId { get; set; }
}
