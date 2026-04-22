using Discovery.Core.Enums.Identity;

namespace Discovery.Core.Entities.Identity;

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

    /// <summary>
    /// Mascara de direitos MeshCentral definida na role.
    /// Quando preenchida, possui precedencia sobre perfil inferido.
    /// </summary>
    public int? MeshRightsMask { get; set; }

    /// <summary>
    /// Perfil de rights MeshCentral vinculado diretamente na role (ex.: viewer/operator/admin).
    /// Usado quando MeshRightsMask nao estiver definido.
    /// </summary>
    public string? MeshRightsProfile { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
