using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Serviço de cache para KPIs de tickets, com invalidação on-write.
/// </summary>
public interface ITicketKpiCacheService
{
    /// <summary>
    /// Obtém KPIs do cache ou executa a factory e armazena.
    /// </summary>
    Task<TicketKpiResult> GetOrComputeAsync(
        Guid? clientId,
        Guid? departmentId,
        DateTime? since,
        Func<Task<TicketKpiResult>> factory,
        CancellationToken ct = default);

    /// <summary>
    /// Invalida o cache de KPIs para um determinado cliente.
    /// Chamado após criação/atualização/fechamento de ticket.
    /// </summary>
    Task InvalidateAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    /// Invalida todas as entradas de cache de KPI.
    /// </summary>
    Task InvalidateAllAsync(CancellationToken ct = default);
}
