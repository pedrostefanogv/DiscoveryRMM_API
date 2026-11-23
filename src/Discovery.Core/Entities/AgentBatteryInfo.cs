namespace Discovery.Core.Entities;

/// <summary>
/// Bateria individual do agent (Win32_Battery + GetSystemPowerStatus).
///
/// É uma lista porque notebooks podem ter mais de uma bateria (ex.: linha
/// ThinkPad com bateria interna + removível). Desktop/VM simplesmente não têm
/// itens.
///
/// Os campos <see cref="OnAC"/> e <see cref="Present"/> são NULLABLE de
/// propósito: NULL = "não reportado" (agente antigo ou SO que não soube
/// responder), nunca "falso". Sem essa distinção o painel mostraria "na
/// bateria" ou "sem bateria" a partir de leitura que falhou.
/// </summary>
public class AgentBatteryInfo
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }

    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? Chemistry { get; set; }

    /// <summary>Rótulo do estado reportado ("Charging", "On AC", "Discharging"...).</summary>
    public string? State { get; set; }

    /// <summary>Presença física da bateria; null = não reportado.</summary>
    public bool? Present { get; set; }

    /// <summary>Máquina na tomada no momento da coleta; null = não reportado.</summary>
    public bool? OnAC { get; set; }

    public bool Charging { get; set; }
    public bool Charged { get; set; }

    /// <summary>Carga atual em % (0-100).</summary>
    public int PercentRemaining { get; set; }

    /// <summary>Capacidade de projeto em mAh (0 = não reportada).</summary>
    public int DesignedCapacityMAh { get; set; }

    /// <summary>Capacidade máxima atual em mAh (0 = não reportada). Saúde = máxima/desenho.</summary>
    public int MaxCapacityMAh { get; set; }

    public int CurrentCapacityMAh { get; set; }

    /// <summary>Saúde reportada quando disponível (ex.: "Good").</summary>
    public string? Health { get; set; }

    public int CycleCount { get; set; }

    public int MinutesUntilEmpty { get; set; }
    public int MinutesToCharge { get; set; }

    public DateTime CollectedAt { get; set; }
}
