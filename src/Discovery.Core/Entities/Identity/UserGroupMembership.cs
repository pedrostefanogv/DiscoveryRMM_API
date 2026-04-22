namespace Discovery.Core.Entities.Identity;

/// <summary>
/// Tabela de junção: usuário pertence a grupo.
/// </summary>
public class UserGroupMembership
{
    public Guid UserId { get; set; }
    public Guid GroupId { get; set; }
    public DateTime JoinedAt { get; set; }
}
