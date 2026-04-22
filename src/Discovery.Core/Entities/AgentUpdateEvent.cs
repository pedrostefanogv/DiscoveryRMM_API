using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class AgentUpdateEvent
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid? AgentReleaseId { get; set; }
    public AgentUpdateEventType EventType { get; set; }
    public string? CurrentVersion { get; set; }
    public string? TargetVersion { get; set; }
    public string? Message { get; set; }
    public string? CorrelationId { get; set; }
    public string? DetailsJson { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Agent? Agent { get; set; }
}
