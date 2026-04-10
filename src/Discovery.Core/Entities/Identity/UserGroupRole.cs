using Discovery.Core.Enums.Identity;

namespace Discovery.Core.Entities.Identity;

/// <summary>
/// Atribuição de uma role a um grupo de usuários em um determinado escopo.
/// Um grupo pode ter múltiplas roles em múltiplos escopos.
/// Ex: Grupo "Suporte" tem role Operator no escopo Client=ClienteX.
/// </summary>
public class UserGroupRole
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid RoleId { get; set; }

    /// <summary>Nível de escopo da atribuição.</summary>
    public ScopeLevel ScopeLevel { get; set; } = ScopeLevel.Global;

    /// <summary>
    /// ID do Client ou Site conforme ScopeLevel.
    /// Null quando ScopeLevel=Global.
    /// </summary>
    public Guid? ScopeId { get; set; }

    public DateTime AssignedAt { get; set; }
}
