namespace Discovery.Core.Entities;

/// <summary>
/// Proposta de recalibração dos pesos do score, com a evidência (taxa de
/// override por dimensão). Aplicada automaticamente no modo Automático ou
/// aguardando decisão do gestor no modo Sugerir.
/// </summary>
public class AiWeightSuggestion
{
    public Guid Id { get; set; }
    public Guid DepartmentId { get; set; }
    public int CycleDays { get; set; } = 7;
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
    public string CurrentWeightsJson { get; set; } = "{}";
    public string SuggestedWeightsJson { get; set; } = "{}";
    public string? EvidenceJson { get; set; }
    public string Status { get; set; } = "pending";
    public bool AutoApplied { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public Guid? DecidedByUserId { get; set; }
}
