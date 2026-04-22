using Discovery.Core.Enums;

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

public class CreateAgentLabelRuleRequest
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
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

public class AgentLabelRuleResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
    public AgentLabelApplyMode ApplyMode { get; set; }
    public AgentLabelRuleExpressionNodeDto Expression { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AgentLabelRuleDryRunRequest
{
    public Guid AgentId { get; set; }
    public string? Label { get; set; }
    public AgentLabelApplyMode ApplyMode { get; set; } = AgentLabelApplyMode.ApplyAndRemove;
    public AgentLabelRuleExpressionNodeDto Expression { get; set; } = new();
}

public class AgentLabelRuleDryRunResponse
{
    public Guid AgentId { get; set; }
    public bool Matched { get; set; }
    public string? Label { get; set; }
    public bool WouldAddLabel { get; set; }
    public bool WouldRemoveLabel { get; set; }
    public IReadOnlyList<string> CurrentAutomaticLabels { get; set; } = [];
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

/// <summary>Summary of a custom field definition usable as a label rule condition.</summary>
public class LabelRuleCustomFieldSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public CustomFieldScopeType ScopeType { get; set; }
    public CustomFieldDataType DataType { get; set; }
}
