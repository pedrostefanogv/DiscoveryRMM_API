using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Métricas históricas por atendente usadas pela triagem por IA. Lê do snapshot
/// materializado quando fresco e recalcula sob demanda quando vencido/ausente.
/// </summary>
public interface ITechnicianMetricsService
{
    /// <summary>Métricas do atendente (snapshot fresco ou recalculado).</summary>
    Task<TechnicianMetricsDto> GetMetricsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Métricas de vários atendentes em lote.</summary>
    Task<IReadOnlyList<TechnicianMetricsDto>> GetMetricsForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Recalcula e persiste snapshots. Sem filtros, recalcula para todos os usuários
    /// que possuem chamados. Retorna a quantidade de snapshots gravados.
    /// </summary>
    Task<int> RefreshSnapshotsAsync(
        IReadOnlyCollection<Guid>? userIds = null,
        Guid? departmentId = null,
        CancellationToken ct = default);
}
