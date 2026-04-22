using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class AgentMonitoringEvent
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid AgentId { get; set; }
    public string AlertCode { get; set; } = string.Empty;
    public MonitoringEventSeverity Severity { get; set; } = MonitoringEventSeverity.Warning;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? MetricKey { get; set; }
    public decimal? MetricValue { get; set; }
    public string? PayloadJson { get; set; }
    public string? LabelsSnapshotJson { get; set; }
    public MonitoringEventSource Source { get; set; } = MonitoringEventSource.Manual;
    public Guid? SourceRefId { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime CreatedAt { get; set; }
}