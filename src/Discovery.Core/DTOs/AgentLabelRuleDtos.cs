using System.Text.Json.Serialization;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;

namespace Discovery.Core.DTOs;

public class AgentLabelRuleExpressionNodeDto
{
    public AgentLabelNodeType NodeType { get; set; }
    public AgentLabelLogicalOperator? LogicalOperator { get; set; }
    public List<AgentLabelRuleExpressionNodeDto> Children { get; set; } = [];
    public AgentLabelField? Field { get; set; }
    /// <summary>Required when Field is AgentCustomField, ClientCustomField or SiteCustomField.</summary>
    public Guid? CustomFieldDefinitionId { get; set; }
    public AgentLabelComparisonOperator? Operator { get; set; }
    public string? Value { get; set; }
}

/// <summary>Payload de criacao de regra. Espelha exatamente o que a UI envia.</summary>
public class CreateAgentLabelRuleRequest
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public AgentLabelApplyMode ApplyMode { get; set; } = AgentLabelApplyMode.ApplyAndRemove;
    public AgentLabelRuleExpressionNodeDto Expression { get; set; } = new();
}

public class UpdateAgentLabelRuleRequest
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public AgentLabelApplyMode ApplyMode { get; set; } = AgentLabelApplyMode.ApplyAndRemove;
    public AgentLabelRuleExpressionNodeDto Expression { get; set; } = new();
}

public class AgentLabelRuleDryRunRequest
{
    public Guid AgentId { get; set; }
    public string? Label { get; set; }
    public AgentLabelApplyMode ApplyMode { get; set; } = AgentLabelApplyMode.ApplyAndRemove;
    public AgentLabelRuleExpressionNodeDto Expression { get; set; } = new();
}

/// <summary>
/// Previa de uma regra para VARIOS agentes em uma unica chamada. A UI disparava
/// um POST /rules/dry-run por agente (ate 100 requisicoes concorrentes por clique).
/// </summary>
public class AgentLabelRuleDryRunBatchRequest
{
    public string? Label { get; set; }
    public AgentLabelApplyMode ApplyMode { get; set; } = AgentLabelApplyMode.ApplyAndRemove;
    public AgentLabelRuleExpressionNodeDto Expression { get; set; } = new();
    public IReadOnlyCollection<Guid> AgentIds { get; set; } = [];
}

/// <summary>
/// Previa de impacto de uma regra em toda a frota (amostragem), para responder
/// "quantos agentes esta regra afetaria?" antes de salvar.
/// </summary>
public class AgentLabelRuleImpactRequest
{
    public string? Label { get; set; }
    public AgentLabelApplyMode ApplyMode { get; set; } = AgentLabelApplyMode.ApplyAndRemove;
    public AgentLabelRuleExpressionNodeDto Expression { get; set; } = new();
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    /// <summary>Quantos agentes avaliar na amostra (clamp 10..500).</summary>
    public int SampleSize { get; set; } = 100;
}

public class AgentLabelRuleImpactResponse
{
    public int Sampled { get; set; }
    public int Matched { get; set; }
    public int WouldAddLabel { get; set; }
    public int WouldRemoveLabel { get; set; }
    /// <summary>Estimativa para a frota inteira, extrapolada da amostra.</summary>
    public int EstimatedTotalAgents { get; set; }
    public int EstimatedMatched { get; set; }
    public IReadOnlyList<AgentLabelRuleImpactSample> Samples { get; set; } = [];
    public bool Truncated { get; set; }
}

public class AgentLabelRuleImpactSample
{
    public Guid AgentId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool Matched { get; set; }
    public bool WouldAddLabel { get; set; }
    public bool WouldRemoveLabel { get; set; }
    public IReadOnlyList<string> CurrentAutomaticLabels { get; set; } = [];
}

public class AgentLabelRuleDryRunResponse
{
    public Guid AgentId { get; set; }
    public bool Matched { get; set; }
    public string? Label { get; set; }
    public bool WouldAddLabel { get; set; }
    public bool WouldRemoveLabel { get; set; }
    public IReadOnlyList<string> CurrentAutomaticLabels { get; set; } = [];

    /// <summary>
    /// Diagnostico da previa: condicoes avaliadas como FALSAS para este agente.
    /// Preenchido apenas quando nao houve match (dry-run explicado).
    /// </summary>
    public IReadOnlyList<string> FailedConditions { get; set; } = [];
}

public class AgentLabelRuleAgentResponse
{
    public Guid AgentId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public AgentStatus Status { get; set; }
    public DateTime MatchedAt { get; set; }
    public DateTime LastEvaluatedAt { get; set; }
}

/// <summary>Composite response for "agents matched by a rule". Matches the frontend contract.</summary>
public class AgentLabelRuleAgentsResponse
{
    public Guid RuleId { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Total de agentes que casam com a regra (contado no banco, nao pela lista materializada).</summary>
    public int TotalAgents { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 100;
    public IReadOnlyList<AgentLabelRuleAgentResponse> Agents { get; set; } = [];
}

/// <summary>
/// Snapshot de uma regra (auditoria de configuracao). A expressao vai como OBJETO,
/// no mesmo formato do editor, para permitir diff na UI.
/// </summary>
public class LabelRuleVersionDto
{
    public Guid Id { get; set; }
    public Guid RuleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
    public string ApplyMode { get; set; } = string.Empty;
    public AgentLabelRuleExpressionNodeDto Expression { get; set; } = new();
    public string? ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; }
}

/// <summary>Requisicao de consulta de labels em lote.</summary>
public class ListAgentLabelsBatchRequest
{
    public IReadOnlyCollection<Guid> AgentIds { get; set; } = [];
}

/// <summary>
/// Representacao portavel de uma regra (import/export entre ambientes). A expressao
/// viaja como OBJETO, no mesmo formato usado pelo editor da UI.
/// </summary>
public class AgentLabelRuleExportDto
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Aceita "ApplyOnly" (export) ou 0 (arquivo editado a mao).</summary>
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string ApplyMode { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public AgentLabelRuleExpressionNodeDto Expression { get; set; } = new();
}

public class AgentLabelRuleImportRequest
{
    public IReadOnlyList<AgentLabelRuleExportDto> Rules { get; set; } = [];

    /// <summary>Quando true, atualiza regras existentes com o mesmo nome (case-insensitive).</summary>
    public bool OverwriteExisting { get; set; }
}

public class AgentLabelRuleImportResultDto
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public IReadOnlyList<string> Errors { get; set; } = [];
}

