namespace Discovery.Core.Entities.Identity;

/// <summary>
/// Tabela de junção: role possui permissões.
/// </summary>
public class RolePermission
{
    public Guid RoleId { get; set; }
    public Guid PermissionId { get; set; }
}
