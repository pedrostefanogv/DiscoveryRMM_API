using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Métricas históricas por atendente usadas pela triagem por IA. A atualização
/// acontece no ciclo agendado (RefreshDueAsync); a leitura usa o snapshot
/// materializado — snapshot vencido NÃO é recalculado dentro de requisição.
/// </summary>
public interface ITechnicianMetricsService
{
    /// <summary>
    /// Métricas do atendente a partir do snapshot. Se nunca houve snapshot e
    /// BootstrapMissingSnapshots estiver ligado, calcula uma vez e grava.
    /// </summary>
    Task<TechnicianMetricsDto> GetMetricsAsync(
        Guid userId, Guid? clientScope = null, CancellationToken ct = default);

    /// <summary>Métricas de vários atendentes em lote (mesma semântica acima).</summary>
    Task<IReadOnlyList<TechnicianMetricsDto>> GetMetricsForUsersAsync(
        IReadOnlyCollection<Guid> userIds, Guid? clientScope = null, CancellationToken ct = default);

    /// <summary>
    /// Recalcula e persiste snapshots de alvos explícitos (ação administrativa /
    /// endpoint de refresh por departamento). Retorna quantos gravou.
    /// </summary>
    Task<int> RefreshSnapshotsAsync(
        IReadOnlyCollection<Guid>? userIds = null,
        Guid? departmentId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Ciclo periódico: percorre os escopos de cliente vencidos e atualiza até
    /// BatchSize usuários por lote, respeitando MaxBatchesPerRun e MaxRunSeconds.
    /// </summary>
    /// <summary>
    /// Ciclo periódico. Com <paramref name="force"/> = true (acionamento manual)
    /// o ciclo ignora o vencimento do escopo E a validade do snapshot, recalculando
    /// os alvos agora — o tick agendado mantém o comportamento normal.
    /// </summary>
    Task<MetricsRefreshResult> RefreshDueAsync(CancellationToken ct = default, bool force = false);

    /// <summary>
    /// Backfill: recálculo FORÇADO (ignora intervalo e vencimento) da janela
    /// configurada. Processa até <paramref name="maxUsers"/> usuários ainda não
    /// recalculados desde <paramref name="sessionStartUtc"/> — a sessão funciona
    /// como cursor, então a operação é retomável e idempotente.
    /// </summary>
    Task<MetricsBackfillProgress> RefreshForcedAsync(
        Guid? clientId, DateTime sessionStartUtc, int maxUsers, CancellationToken ct = default);

    /// <summary>
    /// Remove snapshots de usuários que não são alvo de nenhum escopo (sem vínculo
    /// ativo e sem chamados atribuídos). Retorna quantos removeu.
    /// </summary>
    Task<int> PurgeOrphanSnapshotsAsync(CancellationToken ct = default);
}
