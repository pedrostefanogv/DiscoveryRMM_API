using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Amostra bruta de um sensor (fonte dos gráficos de curto prazo). Retenção
/// curta (default 7 dias); agregados de longo prazo ficam em AgentSensorRollup.
/// </summary>
public class AgentSensorSample
{
    public long Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid SensorId { get; set; }
    public string TargetKey { get; set; } = string.Empty;

    /// <summary>Horário informado pelo agente (diagnóstico).</summary>
    public DateTime CollectedAt { get; set; }
    /// <summary>Horário de recebimento no servidor (base das consultas/gráficos).</summary>
    public DateTime CapturedAt { get; set; }

    public SensorStatus Status { get; set; }
    public SensorSeverity? Severity { get; set; }

    /// <summary>Canais numéricos da amostra (JSON objeto: canal -> número).</summary>
    public string? ChannelsJson { get; set; }
    public string? Message { get; set; }

    public Guid RunId { get; set; }
    public int? ExitCode { get; set; }
}
