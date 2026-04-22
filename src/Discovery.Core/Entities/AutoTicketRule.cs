using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class AutoTicketRule
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int PriorityOrder { get; set; }
    public AutoTicketScopeLevel ScopeLevel { get; set; } = AutoTicketScopeLevel.Global;
    public Guid? ScopeId { get; set; }
    public string? AlertCodeFilter { get; set; }
    public MonitoringEventSource? SourceFilter { get; set; }
    public MonitoringEventSeverity? SeverityMin { get; set; }
    public MonitoringEventSeverity? SeverityMax { get; set; }
    public string? MatchLabelsAnyJson { get; set; }
    public string? MatchLabelsAllJson { get; set; }
    public string? ExcludeLabelsJson { get; set; }
    public string? PayloadPredicateJson { get; set; }
    public AutoTicketRuleAction Action { get; set; } = AutoTicketRuleAction.AlertOnly;
    public Guid? TargetDepartmentId { get; set; }
    public Guid? TargetWorkflowProfileId { get; set; }
    public string? TargetCategory { get; set; }
    public TicketPriority? TargetPriority { get; set; }
    public int DedupWindowMinutes { get; set; } = 60;
    public int CooldownMinutes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}