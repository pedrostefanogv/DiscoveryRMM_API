namespace Discovery.Core.Entities;

/// <summary>
/// Sugestão de competências derivada do histórico de chamados resolvidos do
/// atendente. Fica pendente até o gestor aplicar/descartar quando o modo é
/// Sugerir; no modo Automático é aplicada na criação.
/// </summary>
public class TechnicianSkillSuggestion
{
    public Guid Id { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid UserId { get; set; }
    public int WindowDays { get; set; } = 90;
    public string SuggestedTagsJson { get; set; } = "[]";
    /// <summary>Tags efetivamente aplicadas no perfil (quando aplicada).</summary>
    public string? AppliedTagsJson { get; set; }
    /// <summary>Evidência agregada (contagem por tag/categoria + período), sem texto bruto.</summary>
    public string? EvidenceJson { get; set; }
    public string Status { get; set; } = "pending";
    public bool AutoApplied { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public Guid? DecidedByUserId { get; set; }
}
