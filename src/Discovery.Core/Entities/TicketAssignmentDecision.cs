namespace Discovery.Core.Entities;

/// <summary>
/// Registro auditável de uma decisão da triagem por IA (ou do fallback), com os
/// candidatos avaliados, score, confiança e justificativa. Permite explicar
/// "por que este atendente" e medir a taxa de override manual.
/// </summary>
public class TicketAssignmentDecision
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Guid DepartmentId { get; set; }

    /// <summary>Modo vigente no momento da decisão (0=Sugerir, 1=Atribuir).</summary>
    public int Mode { get; set; }

    /// <summary>Origem da decisão (AiAssignmentDecisionSource).</summary>
    public string StrategySource { get; set; } = string.Empty;

    /// <summary>Dificuldade estimada do chamado (1..5).</summary>
    public int Difficulty { get; set; }

    public Guid? ChosenUserId { get; set; }

    public double Confidence { get; set; }
    public double Score { get; set; }

    /// <summary>Candidatos avaliados com os sub-scores (JSON).</summary>
    public string? CandidatesJson { get; set; }

    public string? Rationale { get; set; }
    public string? Model { get; set; }
    public int TokensUsed { get; set; }

    /// <summary>Teto de tokens de saída usado nesta decisão (auditoria do orçamento).</summary>
    public int MaxOutputTokens { get; set; }

    /// <summary>Caracteres do prompt enviado ao modelo (auditoria do orçamento).</summary>
    public int PromptChars { get; set; }

    /// <summary>
    /// Idade (minutos) do snapshot de métricas usado na decisão. Null = não havia
    /// snapshot (métricas neutras). Ajuda a diagnosticar qualidade da triagem.
    /// </summary>
    public int? MetricsSnapshotAgeMinutes { get; set; }

    /// <summary>True quando a decisão foi efetivamente aplicada ao chamado.</summary>
    public bool Applied { get; set; }

    /// <summary>Motivo de não aplicação/skip (ex.: já possuía responsável manual).</summary>
    public string? NotAppliedReason { get; set; }

    public DateTime? OverriddenAt { get; set; }
    public Guid? OverriddenByUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}
