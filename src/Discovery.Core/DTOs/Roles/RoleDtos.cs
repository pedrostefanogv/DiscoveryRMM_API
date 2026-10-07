namespace Discovery.Core.DTOs.Roles;

/// <summary>
/// Corpo de POST /api/v1/roles/{id}/permissions.
/// Os demais DTOs de role vivem em Discovery.Core.Cqrs.Roles (RoleDto/PermissionDto);
/// as classes duplicadas que existiam aqui nunca eram referenciadas.
/// </summary>
public class AssignPermissionToRoleDto
{
    public Guid PermissionId { get; set; }
}
