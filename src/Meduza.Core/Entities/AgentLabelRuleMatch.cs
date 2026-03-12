namespace Meduza.Core.Entities;

public class AgentLabelRuleMatch
{
    public Guid Id { get; set; }
    public Guid RuleId { get; set; }
    public Guid AgentId { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateTime MatchedAt { get; set; }
    public DateTime LastEvaluatedAt { get; set; }
}
