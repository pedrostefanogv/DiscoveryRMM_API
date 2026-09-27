namespace Discovery.Core.Entities;

/// <summary>Tipos de escopo de processamento em segundo plano rastreados.</summary>
public static class ProcessingScopeTypes
{
    public const string TechnicianMetrics = "technician_metrics";
    public const string TicketTriage = "ticket_triage";
}

/// <summary>
/// Estado do último ciclo de um processamento por escopo (cliente).
/// Fonte única de verdade do "o escopo já venceu?" e da observabilidade por
/// cliente (última execução, itens processados, pendentes restantes).
/// Escopo global usa ScopeId = Guid.Empty.
/// </summary>
public class ProcessingScopeState
{
    public Guid Id { get; set; }

    /// <summary>ProcessingScopeTypes.*</summary>
    public string ScopeType { get; set; } = string.Empty;

    /// <summary>ClientId do escopo; Guid.Empty = global/sem cliente.</summary>
    public Guid ScopeId { get; set; }

    public DateTime LastRunAt { get; set; }

    /// <summary>Resumo do último ciclo (JSON) para a UI/suporte.</summary>
    public string? LastResultJson { get; set; }

    public DateTime UpdatedAt { get; set; }
}
