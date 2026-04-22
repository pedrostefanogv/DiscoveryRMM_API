using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IP2pService
{
    // ─── Agent endpoints ────────────────────────────────────────────────────

    /// <summary>
    /// Retorna (ou calcula) o seed-plan para o site do agente.
    /// </summary>
    Task<P2pSeedPlanResponseDto> GetSeedPlanAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>
    /// Valida e persiste snapshot de telemetria P2P enviado pelo agente.
    /// Retorna lista de erros de validação (vazia = ok).
    /// </summary>
    Task<List<P2pErrorDetail>> IngestTelemetryAsync(
        Guid agentId,
        P2pTelemetryRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Verifica rate limit de telemetria para o agentId (janela 10min / 5 req).
    /// Retorna o número de segundos a aguardar, ou 0 se dentro do limite.
    /// </summary>
    Task<int> CheckTelemetryRateLimitAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>
    /// Retorna status de distribuição de artifacts para o site do agente.
    /// </summary>
    Task<(List<P2pDistributionStatusItem> Items, int Total)> GetDistributionStatusAsync(
        Guid agentId,
        string? artifactId,
        int limit,
        int offset,
        CancellationToken ct = default);

    // ─── Ops / Dashboard endpoints ───────────────────────────────────────────

    /// <summary>
    /// KPIs resumidos por escopo: global / tenant / site / agent.
    /// </summary>
    Task<P2pOverviewDto> GetOverviewAsync(
        string scope,
        Guid? tenantId,
        Guid? siteId,
        Guid? agentId,
        TimeSpan window,
        CancellationToken ct = default);

    /// <summary>
    /// Série temporal de uma métrica específica por escopo.
    /// </summary>
    Task<P2pTimeseriesDto> GetTimeseriesAsync(
        string scope,
        Guid? tenantId,
        Guid? siteId,
        Guid? agentId,
        string metric,
        DateTime from,
        DateTime to,
        TimeSpan interval,
        CancellationToken ct = default);

    /// <summary>
    /// Lista artifacts com distribuição por escopo (site/tenant).
    /// </summary>
    Task<(List<P2pDistributionStatusItem> Items, int Total)> GetArtifactDistributionOpsAsync(
        string scope,
        Guid? tenantId,
        Guid? siteId,
        string? artifactId,
        int limit,
        int offset,
        CancellationToken ct = default);

    /// <summary>
    /// Ranking de agentes por métricas de saúde P2P.
    /// </summary>
    Task<List<P2pAgentRankingItem>> GetAgentRankingAsync(
        string scope,
        Guid? tenantId,
        Guid? siteId,
        TimeSpan window,
        string sortBy,
        CancellationToken ct = default);

    /// <summary>
    /// Seed plans atuais por escopo.
    /// </summary>
    Task<List<P2pSeedPlanHistoryItem>> GetSeedPlanStatusAsync(
        string scope,
        Guid? tenantId,
        Guid? siteId,
        CancellationToken ct = default);
}
