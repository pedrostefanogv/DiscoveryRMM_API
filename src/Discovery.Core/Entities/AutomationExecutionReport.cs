using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class AutomationExecutionReport
{
    public Guid Id { get; set; }

    /// <summary>
    /// Null para execuções automáticas (policy-sync: immediate/recurring/user-login/check-in)
    /// que não passam pelo dispatcher de comandos do servidor.
    /// </summary>
    public Guid? CommandId { get; set; }
    public Guid AgentId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? ScriptId { get; set; }
    public AutomationExecutionSourceType SourceType { get; set; } = AutomationExecutionSourceType.RunNow;
    public AutomationExecutionStatus Status { get; set; } = AutomationExecutionStatus.Dispatched;
    public string? CorrelationId { get; set; }
    public string? RequestMetadataJson { get; set; }
    public string? AckMetadataJson { get; set; }
    public string? ResultMetadataJson { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime? ResultReceivedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
