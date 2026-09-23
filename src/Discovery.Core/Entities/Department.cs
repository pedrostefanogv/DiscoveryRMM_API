namespace Discovery.Core.Entities;

/// <summary>
/// Departamento de um cliente para organizar chamados.
/// Pode ser global (ClientId = null) ou específico de um cliente.
/// Suporta herança de departamentos globais.
/// </summary>
public class Department
{
    public Guid Id { get; set; }
    
    /// <summary>
    /// ClientId do propriedário. Null = Departamento global.
    /// </summary>
    public Guid? ClientId { get; set; }
    
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    
    /// <summary>
    /// Se preenchido, este departamento herda configurações de um global.
    /// </summary>
    public Guid? InheritFromGlobalId { get; set; }
    
    public int SortOrder { get; set; } = 0;
    public bool IsActive { get; set; } = true;

    /// <summary>Estratégia de auto-atribuição (0=None, 1=RoundRobin, 2=LeastOpenTickets).</summary>
    public int AssignmentStrategy { get; set; } = 0;

    /// <summary>Último usuário atribuído (cursor do round-robin).</summary>
    public Guid? RoundRobinLastUserId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
