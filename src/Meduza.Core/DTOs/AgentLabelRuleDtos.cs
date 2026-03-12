using Meduza.Core.Enums;

namespace Meduza.Core.DTOs;

public class AgentLabelRuleExpressionNodeDto
{
    public AgentLabelNodeType NodeType { get; set; }
    public AgentLabelLogicalOperator? LogicalOperator { get; set; }
    public List<AgentLabelRuleExpressionNodeDto> Children { get; set; } = [];
    public AgentLabelField? Field { get; set; }
    public AgentLabelComparisonOperator? Operator { get; set; }
    public string? Value { get; set; }
}

public class CreateAgentLabelRuleRequest
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public AgentLabelApplyMode ApplyMode { get; set; } = AgentLabelApplyMode.ApplyAndRemove;
    public AgentLabelRuleExpressionNodeDto Expression { get; set; } = new();
}

public class UpdateAgentLabelRuleRequest
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public AgentLabelApplyMode ApplyMode { get; set; } = AgentLabelApplyMode.ApplyAndRemove;
    public AgentLabelRuleExpressionNodeDto Expression { get; set; } = new();
}

public class AgentLabelRuleResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public AgentLabelApplyMode ApplyMode { get; set; }
    public AgentLabelRuleExpressionNodeDto Expression { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
