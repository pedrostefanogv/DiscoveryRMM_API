using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>Linha da timeline do sensor: transições de estado e erros de execução.</summary>
public class AgentSensorEvent
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid SensorId { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public Guid RunId { get; set; }

    public SensorEventKind Kind { get; set; }
    public SensorStatus Status { get; set; }
    public SensorSeverity? Severity { get; set; }
    public SensorStatus? PreviousStatus { get; set; }
    public SensorSeverity? PreviousSeverity { get; set; }

    public string? Message { get; set; }
    public string? ChannelsJson { get; set; }
    public int? ExitCode { get; set; }

    public DateTime OccurredAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
