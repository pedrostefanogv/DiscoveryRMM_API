namespace Discovery.Core.Entities;

/// <summary>
/// Ticket de suporte/chamado.
/// Vinculado a Client, opcionalmente a Site e Agent.
/// Pode ter Departamento e Workflow Profile específicos.
/// </summary>
public class Ticket
{
    public Guid Id { get; set; }
    
    // Vinculação de contexto
    public Guid ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }
    
    // Organização interna
    public Guid? DepartmentId { get; set; }
    public Guid? WorkflowProfileId { get; set; }
    
    // Conteúdo
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Category { get; set; }
    
    // Workflow & Prioridade
    public Guid WorkflowStateId { get; set; }
    public Enums.TicketPriority Priority { get; set; } = Enums.TicketPriority.Medium;
    
    // Atribuição (novo: Guid em vez de string)
    public Guid? AssignedToUserId { get; set; }
    
    // SLA Tracking
    public DateTime? SlaExpiresAt { get; set; }
    public bool SlaBreached { get; set; } = false;
    
    // Avaliação/Rating (0-5 estrelas)
    public int? Rating { get; set; } // Null = não avaliado, 0-5 = avaliação
    public DateTime? RatedAt { get; set; }
    public string? RatedBy { get; set; } // Nome/identificador de quem avaliou
    
    // Timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    
    /// <summary>
    /// Calcula dias abertos (utilitário).
    /// </summary>
    public int? DaysOpen => ClosedAt.HasValue 
        ? (int)(ClosedAt.Value - CreatedAt).TotalDays 
        : (int)(DateTime.UtcNow - CreatedAt).TotalDays;
}
