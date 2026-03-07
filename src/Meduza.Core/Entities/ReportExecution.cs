using Meduza.Core.Enums;

namespace Meduza.Core.Entities;

public class ReportExecution
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public Guid ClientId { get; set; }
    public ReportFormat Format { get; set; }
    public string? FiltersJson { get; set; }
    public ReportExecutionStatus Status { get; set; } = ReportExecutionStatus.Pending;
    public string? ResultPath { get; set; }
    public string? ResultContentType { get; set; }
    public long? ResultSizeBytes { get; set; }
    public int? RowCount { get; set; }
    public string? ErrorMessage { get; set; }
    public int? ExecutionTimeMs { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? CreatedBy { get; set; }
}
