namespace Discovery.Core.Entities;

/// <summary>
/// Snapshot agregado das métricas de um atendente, usado pela triagem por IA.
/// Recalculado periodicamente (Quartz) e sob demanda quando vencido.
/// </summary>
public class TechnicianMetricsSnapshot
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Janela (em dias) considerada no cálculo.</summary>
    public int WindowDays { get; set; } = 90;

    public DateTime ComputedAt { get; set; }

    public int AssignedTotal { get; set; }
    public int ResolvedTotal { get; set; }
    public int OpenNow { get; set; }

    public double? AvgFirstResponseMinutes { get; set; }
    public double? AvgResolutionMinutes { get; set; }
    public double? P90ResolutionMinutes { get; set; }

    /// <summary>Proporção de chamados do atendente que violaram o SLA (0..1).</summary>
    public double SlaBreachRate { get; set; }

    /// <summary>Proporção de chamados resolvidos que foram reabertos (0..1).</summary>
    public double ReopenRate { get; set; }

    public double? CsatAverage { get; set; }
    public int CsatRatedCount { get; set; }

    /// <summary>Dificuldade média (1..5) dos chamados resolvidos, quando classificada.</summary>
    public double? DifficultyAverage { get; set; }

    /// <summary>Categorias mais atendidas (JSON array de strings).</summary>
    public string? TopCategoriesJson { get; set; }

    /// <summary>Tags mais atendidas (JSON array de strings).</summary>
    public string? TopTagsJson { get; set; }
}
