using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Historico (auditoria) de mudancas de labels de agentes.
/// Antes nao existia rastro: ao remover uma label, ela simplesmente desaparecia.
/// </summary>
public class AgentLabelChangeLog
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string Label { get; set; } = string.Empty;
    public AgentLabelSourceType SourceType { get; set; }
    /// <summary>"Added" ou "Removed".</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>Motivo da avaliacao (ex.: periodic-reconciliation, custom-field-value-updated:agent:...).</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Regra que produziu/removeu a label, quando identificavel. Sem isto o historico
    /// nao explica oscilacoes (aplicada/removida em sequencia) — nao da para saber qual
    /// regra causou cada mudanca.
    /// </summary>
    public Guid? RuleId { get; set; }
    public string? Actor { get; set; }
    public DateTime OccurredAt { get; set; }
}
