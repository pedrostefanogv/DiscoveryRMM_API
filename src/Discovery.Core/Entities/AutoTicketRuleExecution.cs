using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class AutoTicketRuleExecution
{
    public Guid Id { get; set; }
    public Guid? RuleId { get; set; }
    public Guid MonitoringEventId { get; set; }
    public Guid? AgentId { get; set; }
    public DateTime EvaluatedAt { get; set; }
    public AutoTicketDecision Decision { get; set; } = AutoTicketDecision.MatchedNoAction;
    public string? Reason { get; set; }
    public Guid? CreatedTicketId { get; set; }
    public string? DedupKey { get; set; }
    public bool DedupHit { get; set; }
    public string? PayloadSnapshotJson { get; set; }
}