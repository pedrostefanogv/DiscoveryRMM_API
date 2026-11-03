namespace Discovery.Core.Entities;

/// <summary>
/// Agregação horária por canal (retenção longa: default 400 dias). O job
/// horário recalcula as últimas 2 horas com upsert idempotente.
/// </summary>
public class AgentSensorRollup
{
    public long Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid SensorId { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;

    /// <summary>Início da hora (UTC, truncado).</summary>
    public DateTime BucketStart { get; set; }

    public double? Avg { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Last { get; set; }

    public int Samples { get; set; }
    public int Failures { get; set; }
    /// <summary>Pior status observado na hora (Pending..Unknown).</summary>
    public int WorstStatus { get; set; }
}
