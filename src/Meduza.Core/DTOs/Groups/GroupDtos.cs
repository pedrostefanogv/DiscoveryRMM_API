using Meduza.Core.Enums.Identity;

namespace Meduza.Core.DTOs.Groups;

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
