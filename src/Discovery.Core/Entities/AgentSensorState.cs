using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Estado corrente de um sensor em um agente (1 linha por agente+sensor+alvo).
/// Para DiskSpace com target vazio ("todos os volumes") existe uma linha por
/// volume, identificada por <see cref="TargetKey"/>.
/// </summary>
public class AgentSensorState
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid SensorId { get; set; }

    /// <summary>Chave do alvo resolvido no agente (ex.: "C:"). Vazio quando único.</summary>
    public string TargetKey { get; set; } = string.Empty;

    public SensorStatus Status { get; set; } = SensorStatus.Pending;
    public SensorSeverity? Severity { get; set; }
    public string? Message { get; set; }

    /// <summary>Valores atuais por canal (JSON objeto: canal -> número).</summary>
    public string? ChannelsJson { get; set; }
    public string? PrimaryChannel { get; set; }
    public double? PrimaryValue { get; set; }

    public int? ExitCode { get; set; }
    public int? DurationMs { get; set; }
    /// <summary>Trecho da saída (stdout/stderr) quando a política de captura permite.</summary>
    public string? OutputExcerpt { get; set; }

    public int ConsecutiveFailures { get; set; }
    public long TotalRuns { get; set; }
    public long TotalFailures { get; set; }

    public DateTime? LastRunAt { get; set; }
    public DateTime? LastStatusChangedAt { get; set; }
    public DateTime? LastAlertedAt { get; set; }
    public SensorSeverity? LastAlertSeverity { get; set; }

    /// <summary>Últimas amostras por canal (JSON objeto: canal -> number[]).</summary>
    public string? SparklineJson { get; set; }

    /// <summary>runId do último resultado aplicado (idempotência de retry).</summary>
    public Guid? LastRunId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
