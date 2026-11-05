using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Registro auditável de uma sugestão da IA no enriquecimento do chamado.
///
/// Espelha TicketAssignmentDecision, que o produto já usa para a triagem de
/// atribuição: mesmo padrão de Confidence/Applied/OverriddenAt, para que a taxa
/// de override manual seja mensurável.
///
/// Guardamos até as sugestões NÃO aplicadas: é o que permite medir quantas a IA
/// acerta, quantas o atendente derruba e por quê.
/// </summary>
public class AutoTicketSuggestion
{
    public Guid Id { get; set; }

    /// <summary>Execução de auto-ticket que originou a sugestão.</summary>
    public Guid? RuleExecutionId { get; set; }

    public Guid MonitoringEventId { get; set; }
    public Guid? TicketId { get; set; }
    public Guid? AgentId { get; set; }

    public AutoTicketSuggestionField Field { get; set; }

    public string? SuggestedValue { get; set; }
    public string? AppliedValue { get; set; }

    public double Confidence { get; set; }

    /// <summary>Modo vigente na decisão (Off/Suggest/AutoApply).</summary>
    public int Mode { get; set; }

    /// <summary>Origem (ver AutoTicketSuggestionSource).</summary>
    public string StrategySource { get; set; } = AutoTicketSuggestionSource.Ai;

    public string? Rationale { get; set; }
    public string? Model { get; set; }
    public int TokensUsed { get; set; }

    /// <summary>True quando a sugestão virou valor real do chamado.</summary>
    public bool Applied { get; set; }

    /// <summary>Motivo de não ter sido aplicada (baixa confiança, inválida, modo Suggest).</summary>
    public string? NotAppliedReason { get; set; }

    /// <summary>Preenchido quando um humano desfez a sugestão aplicada.</summary>
    public DateTime? OverriddenAt { get; set; }
    public Guid? OverriddenByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
