namespace Meduza.Core.Entities;

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
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
