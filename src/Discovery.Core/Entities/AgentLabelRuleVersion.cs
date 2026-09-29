using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Snapshot de uma regra de label a cada escrita (criacao/atualizacao/import).
///
/// O historico de labels por agente registra o EFEITO; este registra a CONFIGURACAO,
/// permitindo auditar quem mudou o que e reverter uma regra alterada por engano.
/// </summary>
public class AgentLabelRuleVersion
{
    public Guid Id { get; set; }
    public Guid RuleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
    public AgentLabelApplyMode ApplyMode { get; set; }
    public string ExpressionJson { get; set; } = string.Empty;
    public string? ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; }
}
