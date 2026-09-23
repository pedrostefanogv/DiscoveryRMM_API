namespace Discovery.Core.Entities;

/// <summary>Membro (usuário) de um departamento — usado pelo round-robin de atribuição.</summary>
public class DepartmentMember
{
    public Guid Id { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid UserId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
