using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Perfil de workflow que define estados, SLA e comportamento para um tipo específico de chamado.
/// Exemplo: "Emergência - TI" (SLA 1h), "Padrão - Compras" (SLA 24h).
/// </summary>
public class WorkflowProfile
{
    public Guid Id { get; set; }
    
    /// <summary>
    /// ClientId do propriedário. Null = Perfil global.
    /// </summary>
    public Guid? ClientId { get; set; }
    
    /// <summary>
    /// Departamento ao qual este perfil se aplica.
    /// </summary>
    public Guid DepartmentId { get; set; }
    
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    
    /// <summary>
    /// SLA em horas. Ex: 1 (emergência), 4 (crítico), 24h (padrão).
    /// </summary>
    public int SlaHours { get; set; } = 24;
    
    /// <summary>
    /// Prioridade padrão para tickets deste perfil.
    /// </summary>
    public TicketPriority DefaultPriority { get; set; } = TicketPriority.Medium;
    
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
