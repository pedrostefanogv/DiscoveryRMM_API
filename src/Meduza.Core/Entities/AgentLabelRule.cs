using Meduza.Core.Enums;

namespace Meduza.Core.Entities;

public class AgentLabelRule
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public AgentLabelApplyMode ApplyMode { get; set; } = AgentLabelApplyMode.ApplyAndRemove;
    public string ExpressionJson { get; set; } = string.Empty;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
