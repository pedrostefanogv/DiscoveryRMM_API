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

    /// <summary>Template usado na abertura do chamado (null = abertura normal).</summary>
    public Guid? TemplateId { get; set; }

    /// <summary>
    /// Snapshot do nome do template no momento da abertura. Preserva o histórico
    /// mesmo se o template for excluído (a FK zera o TemplateId).
    /// </summary>
    public string? TemplateName { get; set; }

    /// <summary>
    /// Snapshot markdown (somente leitura) do formulário/template enviado na
    /// abertura do chamado. Gravado apenas na criação e nunca atualizado.
    /// </summary>
    public string? SubmissionSnapshotMarkdown { get; set; }

    // Workflow & Prioridade
    public Guid WorkflowStateId { get; set; }
    public Enums.TicketPriority Priority { get; set; } = Enums.TicketPriority.Medium;
    
    // Atribuição (novo: Guid em vez de string)
    public Guid? AssignedToUserId { get; set; }
    
    // SLA Tracking
    public DateTime? SlaExpiresAt { get; set; }
    public bool SlaBreached { get; set; } = false;

    /// <summary>Data/hora de expiração do SLA de primeira resposta.</summary>
    public DateTime? SlaFirstResponseExpiresAt { get; set; }

    /// <summary>Quando o atribuído efetivamente respondeu pela primeira vez.</summary>
    public DateTime? FirstRespondedAt { get; set; }

    /// <summary>Segundos acumulados em que o SLA estava pausado (estados PausesSla=true).</summary>
    public int SlaPausedSeconds { get; set; } = 0;

    /// <summary>Início da pausa atual de SLA. Null = SLA não está pausado agora.</summary>
    public DateTime? SlaHoldStartedAt { get; set; }
    
    // Avaliação/Rating (0-5 estrelas)
    public int? Rating { get; set; } // Null = não avaliado, 0-5 = avaliação
    public string? RatingFeedback { get; set; } // Comentário/feedback da avaliação (CSAT)
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
