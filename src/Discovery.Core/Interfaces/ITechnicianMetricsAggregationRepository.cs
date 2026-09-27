namespace Discovery.Core.Interfaces;

/// <summary>Agregados de tickets por atendente (janela + abertos de qualquer época).</summary>
public sealed record TechnicianMetricsAggregateRow(
    Guid UserId,
    int AssignedTotal,
    int ResolvedTotal,
    int OpenNow,
    double? AvgFirstResponseMinutes,
    double? AvgResolutionMinutes,
    double? P90ResolutionMinutes,
    int SlaBreachedInWindow,
    double? CsatAverage,
    int CsatRatedCount);

/// <summary>Categoria mais atendida por um atendente na janela.</summary>
public sealed record TechnicianCategoryCount(Guid UserId, string Category, int Count);

/// <summary>
/// Agregação das métricas por atendente direto no banco. Substitui o cálculo
/// que carregava em memória todos os tickets do lote — o consumo agora é de uma
/// linha por atendente por consulta.
/// </summary>
public interface ITechnicianMetricsAggregationRepository
{
    Task<IReadOnlyList<TechnicianMetricsAggregateRow>> GetAggregatesAsync(
        IReadOnlyCollection<Guid> userIds, DateTime since, CancellationToken ct = default);

    Task<IReadOnlyList<TechnicianCategoryCount>> GetTopCategoriesAsync(
        IReadOnlyCollection<Guid> userIds, DateTime since, int perUser, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, int>> GetReopenCountsAsync(
        IReadOnlyCollection<Guid> userIds, DateTime since, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, double>> GetDifficultyAveragesAsync(
        IReadOnlyCollection<Guid> userIds, DateTime since, CancellationToken ct = default);
}
