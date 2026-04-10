using Discovery.Core.Enums.Identity;

namespace Discovery.Core.DTOs.Roles;

public class CreateRoleDto
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public RoleMfaRequirement MfaRequirement { get; set; } = RoleMfaRequirement.None;
    public int? MeshRightsMask { get; set; }
    public string? MeshRightsProfile { get; set; }
}

public class UpdateRoleDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public RoleMfaRequirement? MfaRequirement { get; set; }
    public int? MeshRightsMask { get; set; }
    public string? MeshRightsProfile { get; set; }
}

public class RoleDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public RoleType Type { get; set; }
    public bool IsSystem { get; set; }
    public RoleMfaRequirement MfaRequirement { get; set; }
    public int? MeshRightsMask { get; set; }
    public string? MeshRightsProfile { get; set; }
    public DateTime CreatedAt { get; set; }
    public IEnumerable<PermissionDto> Permissions { get; set; } = [];
}

public class PermissionDto
{
    public Guid Id { get; set; }
    public ResourceType ResourceType { get; set; }
    public ActionType ActionType { get; set; }
    public string? Description { get; set; }
}

public class AssignPermissionToRoleDto
{
    public Guid PermissionId { get; set; }
}
