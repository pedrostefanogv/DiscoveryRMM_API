using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

public class UpsertAutoTicketRuleRequest
{
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int PriorityOrder { get; set; }
    public AutoTicketScopeLevel ScopeLevel { get; set; } = AutoTicketScopeLevel.Global;
    public Guid? ScopeId { get; set; }
    public string? AlertCodeFilter { get; set; }
    public MonitoringEventSource? SourceFilter { get; set; }
    public MonitoringEventSeverity? SeverityMin { get; set; }
    public MonitoringEventSeverity? SeverityMax { get; set; }
    public IReadOnlyList<string>? MatchLabelsAny { get; set; }
    public IReadOnlyList<string>? MatchLabelsAll { get; set; }
    public IReadOnlyList<string>? ExcludeLabels { get; set; }
    public string? PayloadPredicateJson { get; set; }
    public AutoTicketRuleAction Action { get; set; } = AutoTicketRuleAction.AlertOnly;
    public Guid? TargetDepartmentId { get; set; }
    public Guid? TargetWorkflowProfileId { get; set; }
    public string? TargetCategory { get; set; }
    public TicketPriority? TargetPriority { get; set; }
    public int DedupWindowMinutes { get; set; } = 60;
    public int CooldownMinutes { get; set; }
}

public class AutoTicketRuleDryRunRequest
{
    public Guid ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid AgentId { get; set; }
    public string AlertCode { get; set; } = string.Empty;
    public MonitoringEventSeverity Severity { get; set; } = MonitoringEventSeverity.Warning;
    public string? Title { get; set; }
    public string? Message { get; set; }
    public string? MetricKey { get; set; }
    public decimal? MetricValue { get; set; }
    public string? PayloadJson { get; set; }
    public IReadOnlyList<string>? Labels { get; set; }
    public MonitoringEventSource Source { get; set; } = MonitoringEventSource.Manual;
    public DateTime? OccurredAt { get; set; }
}

public class AutoTicketRuleDryRunResponse
{
    public AutoTicketDecision Decision { get; set; }
    public bool WouldCreateTicket { get; set; }
    public Guid? RuleId { get; set; }
    public string? RuleName { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? DedupKey { get; set; }
}

public class SeedDefaultAutoTicketRulesRequest
{
    public Guid ClientId { get; set; }
    public string InfraDepartmentName { get; set; } = "Infra";
    public string ServiceDeskDepartmentName { get; set; } = "ServiceDesk";
    public bool IsEnabled { get; set; } = true;
    public int DedupWindowMinutes { get; set; } = 60;
}

public class SeededAutoTicketRuleResult
{
    public Guid RuleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }
    public Guid? WorkflowProfileId { get; set; }
    public string? Warning { get; set; }
}

public class SeedDefaultAutoTicketRulesResponse
{
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public IReadOnlyList<SeededAutoTicketRuleResult> Rules { get; set; } = [];
}

public class AutoTicketRuleStatsSnapshot
{
    public int TotalEvaluations { get; set; }
    public int SelectedExecutions { get; set; }
    public int CreatedCount { get; set; }
    public int DedupedCount { get; set; }
    public int SuppressedCount { get; set; }
    public int MatchedNoActionCount { get; set; }
    public int FailedCount { get; set; }
    public int RateLimitedCount { get; set; }
    public DateTime? FirstSelectedAtUtc { get; set; }
    public DateTime? LastSelectedAtUtc { get; set; }
}

public class AutoTicketRuleStatsResponse
{
    public Guid RuleId { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public AutoTicketScopeLevel ScopeLevel { get; set; }
    public Guid? ScopeId { get; set; }
    public string? AlertCodeFilter { get; set; }
    public int PeriodHours { get; set; }
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
    public int TotalEvaluations { get; set; }
    public int SelectedExecutions { get; set; }
    public int CreatedCount { get; set; }
    public int DedupedCount { get; set; }
    public int SuppressedCount { get; set; }
    public int MatchedNoActionCount { get; set; }
    public int FailedCount { get; set; }
    public int RateLimitedCount { get; set; }
    public double MatchRate { get; set; }
    public double DedupRate { get; set; }
    public double CreateRate { get; set; }
    public double FailureRate { get; set; }
    public DateTime? FirstSelectedAtUtc { get; set; }
    public DateTime? LastSelectedAtUtc { get; set; }
}

public class CreateMonitoringEventRequest
{
    public Guid ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid AgentId { get; set; }
    public string AlertCode { get; set; } = string.Empty;
    public MonitoringEventSeverity Severity { get; set; } = MonitoringEventSeverity.Warning;
    public string? Title { get; set; }
    public string? Message { get; set; }
    public string? MetricKey { get; set; }
    public decimal? MetricValue { get; set; }
    public string? PayloadJson { get; set; }
    public IReadOnlyList<string>? Labels { get; set; }
    public MonitoringEventSource Source { get; set; } = MonitoringEventSource.Manual;
    public Guid? SourceRefId { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime? OccurredAt { get; set; }
    public bool EvaluateAutoTicket { get; set; } = true;
}

public class MonitoringEventIngestionResponse
{
    public Guid MonitoringEventId { get; set; }
    public Guid? RuleId { get; set; }
    public AutoTicketDecision? Decision { get; set; }
    public Guid? CreatedTicketId { get; set; }
    public string? Reason { get; set; }
}