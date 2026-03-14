using Meduza.Core.Enums;

namespace Meduza.Core.DTOs;

public class AutomationExecutionAckRequest
{
    public Guid? TaskId { get; set; }
    public Guid? ScriptId { get; set; }
    public AutomationExecutionSourceType SourceType { get; set; } = AutomationExecutionSourceType.RunNow;
    public string? MetadataJson { get; set; }
}

public class AutomationExecutionResultRequest
{
    public Guid? TaskId { get; set; }
    public Guid? ScriptId { get; set; }
    public AutomationExecutionSourceType SourceType { get; set; } = AutomationExecutionSourceType.RunNow;
    public bool Success { get; set; }
    public int? ExitCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? MetadataJson { get; set; }
}

public class AutomationExecutionReportDto
{
    public Guid Id { get; set; }
    public Guid CommandId { get; set; }
    public Guid AgentId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? ScriptId { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime? ResultReceivedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? RequestMetadataJson { get; set; }
    public string? AckMetadataJson { get; set; }
    public string? ResultMetadataJson { get; set; }
}
