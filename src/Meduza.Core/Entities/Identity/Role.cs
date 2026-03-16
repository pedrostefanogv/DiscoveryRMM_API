using Meduza.Core.Enums.Identity;

namespace Meduza.Core.Entities.Identity;

public class Role
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public RoleType Type { get; set; } = RoleType.Custom;

    /// <summary>Roles de sistema (seeds) não podem ser editadas ou removidas.</summary>
    public bool IsSystem { get; set; } = false;

    /// <summary>Política de 2FA exigida para usuários vinculados à role.</summary>
    public RoleMfaRequirement MfaRequirement { get; set; } = RoleMfaRequirement.None;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
