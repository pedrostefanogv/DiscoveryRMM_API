namespace Discovery.Core.Entities;

/// <summary>
/// Controladora RAID/ferramenta de storage detectada no hardware do servidor
/// (ex.: PERC, Smart Array, MegaRAID), junto com o resumo de volumes e discos.
///
/// Quando a ferramenta existe mas falha ao executar, <see cref="Ok"/> é false e
/// <see cref="Error"/> traz o motivo — o site usa isso para diferenciar
/// "não tem RAID" de "não consegui ler o RAID".
/// </summary>
public class AgentRaidController
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }

    /// <summary>Nome da ferramenta/controladora (ex.: "perccli").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Fabricante identificado (ex.: "Dell", "HP", "LSI").</summary>
    public string? Vendor { get; set; }

    /// <summary>Caminho do executável usado na coleta.</summary>
    public string? Path { get; set; }

    /// <summary>true quando a ferramenta executou com sucesso.</summary>
    public bool Ok { get; set; }

    /// <summary>Motivo da falha, quando <see cref="Ok"/> é false.</summary>
    public string? Error { get; set; }

    public int? Controllers { get; set; }
    public int? VirtualDisks { get; set; }
    public int? PhysicalDisks { get; set; }

    /// <summary>
    /// Volumes/discos em estado degradado, serializados como JSON.
    /// A lista é pequena e heterogênea; guardar cru evita perder detalhe.
    /// </summary>
    public string? DegradedJson { get; set; }

    public DateTime CollectedAt { get; set; }
}
