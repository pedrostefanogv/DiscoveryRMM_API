using Meduza.Core.Enums;

namespace Meduza.Core.Entities;

public class AgentLabel
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string Label { get; set; } = string.Empty;
    public AgentLabelSourceType SourceType { get; set; } = AgentLabelSourceType.Automatic;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
