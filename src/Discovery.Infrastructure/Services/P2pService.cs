using System.Data;
using System.Text.RegularExpressions;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação central de telemetria P2P.
/// Validação, persistência de snapshots, upsert de presença de artifact e cálculo de seed-plan.
/// </summary>
public class P2pService : IP2pService
{
    private const long OneTibytes = 1_099_511_627_776L;
    // Caracteres permitidos em peerAgentIds
    private static readonly Regex PeerAgentIdRegex = new(@"^[a-zA-Z0-9\-_.]+$", RegexOptions.Compiled);

    private readonly DiscoveryDbContext _db;
    private readonly IAgentRepository _agentRepo;
    private readonly ISiteRepository _siteRepo;
    private readonly IRedisService _redis;

    public P2pService(
        DiscoveryDbContext db,
        IAgentRepository agentRepo,
        ISiteRepository siteRepo,
        IRedisService redis)
    {
        _db = db;
        _agentRepo = agentRepo;
        _siteRepo = siteRepo;
        _redis = redis;
    }

    // ──────────────────────────────────────────────────────────────────────
    // SEED PLAN
    // ──────────────────────────────────────────────────────────────────────

    public async Task<P2pSeedPlanResponseDto> GetSeedPlanAsync(Guid agentId, CancellationToken ct = default)
    {
        var agent = await _agentRepo.GetByIdAsync(agentId)
            ?? throw new InvalidOperationException("Agent not found");

        var plan = await _db.P2pSeedPlans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.SiteId == agent.SiteId, ct);

        if (plan is null)
        {
            // Calcular on-demand e persistir
            plan = await RecalculateSeedPlanAsync(agent.SiteId, ct);
        }

        return new P2pSeedPlanResponseDto(
            plan.SiteId.ToString(),
            plan.GeneratedAt.ToString("O"),
            new P2pSeedPlanDto(
                plan.TotalAgents,
                plan.ConfiguredPercent,
                plan.MinSeeds,
                plan.SelectedSeeds));
    }

    // internal para testes; clientIdOverride evita a consulta ao repositório de
    // sites quando o chamador já conhece o cliente (ingestão de telemetria).
    internal async Task<P2pSeedPlan> RecalculateSeedPlanAsync(Guid siteId, CancellationToken ct, Guid? clientIdOverride = null)
    {
        Guid clientId;
        if (clientIdOverride.HasValue)
        {
            clientId = clientIdOverride.Value;
        }
        else
        {
            var site = await _siteRepo.GetByIdAsync(siteId);
            clientId = site?.ClientId ?? Guid.Empty;
        }

        // Contar agentes ativos nos últimos 10 minutos
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        var totalAgents = await _db.P2pAgentTelemetries
            .Where(t => t.SiteId == siteId && t.ReceivedAt >= cutoff)
            .Select(t => t.AgentId)
            .Distinct()
            .CountAsync(ct);

        const int configuredPercent = 10;
        const int minSeeds = 2;
        int selectedSeeds = CalculateSelectedSeeds(totalAgents, configuredPercent, minSeeds);

        var existing = await _db.P2pSeedPlans.FirstOrDefaultAsync(p => p.SiteId == siteId, ct);
        if (existing is not null)
        {
            existing.TotalAgents = totalAgents;
            existing.ConfiguredPercent = configuredPercent;
            existing.MinSeeds = minSeeds;
            existing.SelectedSeeds = selectedSeeds;
            existing.GeneratedAt = DateTime.UtcNow;
        }
        else
        {
            existing = new P2pSeedPlan
            {
                SiteId = siteId,
                ClientId = clientId,
                TotalAgents = totalAgents,
                ConfiguredPercent = configuredPercent,
                MinSeeds = minSeeds,
                SelectedSeeds = selectedSeeds,
                GeneratedAt = DateTime.UtcNow
            };
            _db.P2pSeedPlans.Add(existing);
        }

        await _db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>
    /// Recalcula o seed-plan do site quando ele ainda está ausente ou zerado.
    /// Sem isso, um site que acabou de ligar os agentes fica até 15 minutos
    /// (P2pMaintenanceJob) com total_agents/selected_seeds = 0, e o dashboard
    /// mostra "Seeders ativos" e o plano errados. internal para testes.
    /// </summary>
    internal async Task EnsureSeedPlanFreshAsync(Guid siteId, Guid clientId, CancellationToken ct)
    {
        var currentTotal = await _db.P2pSeedPlans
            .AsNoTracking()
            .Where(p => p.SiteId == siteId)
            .Select(p => p.TotalAgents)
            .FirstOrDefaultAsync(ct);

        if (currentTotal > 0) return;

        try
        {
            await RecalculateSeedPlanAsync(siteId, ct, clientId);
        }
        catch (DbUpdateException)
        {
            // Corrida: dois ingests simultâneos do mesmo site tentaram criar o
            // plano (PK = SiteId). O janitor de 15 min recria/atualiza — a
            // ingestão de telemetria não pode falhar por causa disso. Desanexa a
            // entidade pendente para não deixar o DbContext inutilizável.
            foreach (var entry in _db.ChangeTracker.Entries<P2pSeedPlan>()
                         .Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private static int CalculateSelectedSeeds(int totalAgents, int configuredPercent, int minSeeds)
    {
        if (totalAgents == 0) return 0;
        var fromPercent = (int)Math.Ceiling(totalAgents * configuredPercent / 100.0);
        var result = Math.Max(fromPercent, minSeeds);
        return Math.Min(result, totalAgents);
    }

    // ──────────────────────────────────────────────────────────────────────
    // RATE LIMIT (Redis)
    // ──────────────────────────────────────────────────────────────────────

    public async Task<int> CheckTelemetryRateLimitAsync(Guid agentId, CancellationToken ct = default)
    {
        const int windowSeconds = 600; // 10 min
        const int maxRequests = 5;

        var key = $"p2p:rl:telemetry:{agentId}";
        var count = await _redis.IncrementAsync(key);
        if (count <= 0)
            return 0;

        if (count == 1)
        {
            await _redis.SetExpiryAsync(key, windowSeconds);
        }

        var ttl = await _redis.GetTtlSecondsAsync(key);
        if (ttl <= 0)
        {
            await _redis.SetExpiryAsync(key, windowSeconds);
            ttl = windowSeconds;
        }

        if (count > maxRequests)
            return ttl;

        return 0;
    }

    // ──────────────────────────────────────────────────────────────────────
    // TELEMETRIA
    // ──────────────────────────────────────────────────────────────────────

    public async Task<List<P2pErrorDetail>> IngestTelemetryAsync(
        Guid agentId,
        P2pTelemetryRequest request,
        CancellationToken ct = default)
    {
        var agent = await _agentRepo.GetByIdAsync(agentId)
            ?? throw new InvalidOperationException("Agent not found");
        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        var clientId = site?.ClientId ?? Guid.Empty;

        var errors = ValidateTelemetryRequest(request, agentId, agent.SiteId);
        if (errors.Count > 0) return errors;

        var collectedAt = DateTime.Parse(request.CollectedAtUtc!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        var metrics = request.Metrics!;
        var plan = request.CurrentSeedPlan!;
        var hostLoad = request.HostLoad;

        var snapshot = new P2pAgentTelemetry
        {
            AgentId = agentId,
            SiteId = agent.SiteId,
            ClientId = clientId,
            CollectedAt = collectedAt,
            ReceivedAt = DateTime.UtcNow,
            PublishedArtifacts = metrics.PublishedArtifacts,
            ReplicationsStarted = metrics.ReplicationsStarted,
            ReplicationsSucceeded = metrics.ReplicationsSucceeded,
            ReplicationsFailed = metrics.ReplicationsFailed,
            BytesServed = metrics.BytesServed,
            BytesDownloaded = metrics.BytesDownloaded,
            QueuedReplications = metrics.QueuedReplications,
            ActiveReplications = metrics.ActiveReplications,
            AutoDistributionRuns = metrics.AutoDistributionRuns,
            CatalogRefreshRuns = metrics.CatalogRefreshRuns,
            ChunkedDownloads = metrics.ChunkedDownloads,
            ChunksDownloaded = metrics.ChunksDownloaded,
            PreloadSkippedFinalState = metrics.PreloadSkippedFinalState,
            PlanTotalAgents = plan.TotalAgents,
            PlanConfiguredPercent = plan.ConfiguredPercent,
            PlanMinSeeds = plan.MinSeeds,
            PlanSelectedSeeds = plan.SelectedSeeds,
            // ── Telemetria enriquecida ──
            KnownPeers = request.KnownPeers,
            ConnectedPeers = request.ConnectedPeers,
            HostCpuPercent = hostLoad?.CpuPercent,
            HostMemoryPercent = hostLoad?.MemoryPercent,
            HostDiskBusyPercent = hostLoad?.DiskBusyPercent,
            HostCpuCores = hostLoad?.CpuCores ?? 0,
            HostRamGB = hostLoad?.RamGB ?? 0,
        };

        // Idempotência por (agent_id, collected_at): o outbox do agent reenvia o
        // mesmo snapshot quando a resposta se perde ou após restart. Sem isso a
        // amostra entrava duas vezes e os contadores cumulativos eram somados em
        // dobro. O índice único é a rede de segurança para corrida entre requests.
        var alreadyStored = await _db.P2pAgentTelemetries
            .AsNoTracking()
            .AnyAsync(t => t.AgentId == agentId && t.CollectedAt == collectedAt, ct);
        if (!alreadyStored)
        {
            _db.P2pAgentTelemetries.Add(snapshot);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _db.Entry(snapshot).State = EntityState.Detached;
            }
        }

        // ── Upsert de P2pArtifactPresence a partir de Artifacts[] ──
        if (request.Artifacts is { Count: > 0 })
        {
            await UpsertArtifactPresenceAsync(agentId, agent.SiteId, clientId, request.Artifacts, ct);
        }

        // Mantém o seed-plan fresco para o site (apenas quando ainda está
        // zerado/ausente), evitando depender só do job de 15 min.
        await EnsureSeedPlanFreshAsync(agent.SiteId, clientId, ct);

        return errors;
    }

    /// <summary>
    /// Upsert em lote das presenças de artifact reportadas pelo agent.
    ///
    /// Antes era um SELECT por artifact (até 500 por telemetria) — N+1 a cada
    /// ~5 min por agente — e IDs repetidos no payload causavam violação de PK,
    /// porque o SELECT não enxerga entidades ainda pendentes no change tracker.
    /// Agora faz uma única consulta e deduplica por ArtifactId.
    /// internal para testes.
    /// </summary>
    internal async Task UpsertArtifactPresenceAsync(
        Guid agentId,
        Guid siteId,
        Guid clientId,
        IReadOnlyList<P2pArtifactPresenceDto> artifacts,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Resolve + deduplica (a última ocorrência vence).
        var requested = new Dictionary<Guid, (bool IsSynthetic, P2pArtifactPresenceDto Artifact)>();
        foreach (var artifact in artifacts)
        {
            var (presenceId, isSynthetic) = ResolveArtifactPresenceId(artifact.ArtifactId);
            if (presenceId == Guid.Empty) continue;
            requested[presenceId] = (isSynthetic, artifact);
        }
        if (requested.Count == 0) return;

        var ids = requested.Keys.ToList();
        var existingByArtifact = await _db.P2pArtifactPresences
            .Where(p => p.AgentId == agentId && ids.Contains(p.ArtifactId))
            .ToDictionaryAsync(p => p.ArtifactId, ct);

        foreach (var (presenceId, entry) in requested)
        {
            if (existingByArtifact.TryGetValue(presenceId, out var existing))
            {
                existing.LastSeenAt = now;
                existing.ArtifactName = entry.Artifact.ArtifactName;
                existing.IdIsSynthetic = entry.IsSynthetic;
                continue;
            }

            _db.P2pArtifactPresences.Add(new P2pArtifactPresence
            {
                ArtifactId = presenceId,
                ArtifactName = entry.Artifact.ArtifactName,
                IdIsSynthetic = entry.IsSynthetic,
                AgentId = agentId,
                SiteId = siteId,
                ClientId = clientId,
                LastSeenAt = now,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Namespace fixo (UUIDv5) para derivar Guids determinísticos de IDs
    /// sintéticos de artifact enviados pelo agent (ex.: "winget:7zip7zip").
    /// Mesma string → mesmo Guid em qualquer ingest.
    /// </summary>
    private static readonly Guid P2pSyntheticArtifactNamespace =
        new("6f1d0f35-2a5e-4c3b-9a0e-1f2b3c4d5e6f");

    /// <summary>
    /// Converte o artifactId informado pelo agent em (Guid, isSynthetic).
    /// Guid parseável → usado direto (isSynthetic=false). String sintética
    /// (ex.: "winget:7zip7zip") → Guid determinístico via UUIDv5 (MD5) do
    /// namespace acima (isSynthetic=true). Vazio/inválido → (Guid.Empty, false).
    /// </summary>
    private static (Guid Id, bool IsSynthetic) ResolveArtifactPresenceId(string? artifactId)
    {
        var raw = artifactId?.Trim();
        if (string.IsNullOrEmpty(raw)) return (Guid.Empty, false);

        if (Guid.TryParse(raw, out var guid)) return (guid, false);

        // UUIDv5 (RFC 4122): MD5(namespace_bytes + name_bytes), versão 5, variante RFC.
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(raw);
        var nsBytes = P2pSyntheticArtifactNamespace.ToByteArray();
        var hashInput = new byte[nsBytes.Length + nameBytes.Length];
        nsBytes.CopyTo(hashInput, 0);
        nameBytes.CopyTo(hashInput, nsBytes.Length);
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(hashInput);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // version 5
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // variant RFC 4122
        return (new Guid(hash), true);
    }

    // ──────────────────────────────────────────────────────────────────────
    // DISTRIBUTION STATUS (agent endpoint)
    // ──────────────────────────────────────────────────────────────────────

    public async Task<(List<P2pDistributionStatusItem> Items, int Total)> GetDistributionStatusPageAsync(
        Guid agentId,
        Guid? artifactId,
        string? cursor,
        int limit,
        CancellationToken ct = default)
    {
        var agent = await _agentRepo.GetByIdAsync(agentId)
            ?? throw new InvalidOperationException("Agent not found");

        return await QueryDistributionByScope(
            siteId: agent.SiteId,
            clientId: null,
            global: false,
            artifactId: artifactId,
            limit: limit,
            offset: 0,
            cursor: cursor,
            ct: ct);
    }

    // ──────────────────────────────────────────────────────────────────────
    // OPS / DASHBOARD
    // ──────────────────────────────────────────────────────────────────────

    public async Task<P2pOverviewDto> GetOverviewAsync(
        string scope,
        Guid? tenantId,
        Guid? siteId,
        Guid? agentId,
        TimeSpan window,
        CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - window;

        // Deltas reset-aware por agente (ver QueryAgentCounterDeltasAsync): os
        // contadores do agent zeram ao reiniciar, então "última - primeira amostra"
        // devolvia 0 B mesmo com centenas de MB transferidos na janela.
        var deltas = await QueryAgentCounterDeltasAsync(scope, tenantId, siteId, agentId, cutoff, ct);

        if (deltas.Count == 0)
        {
            return new P2pOverviewDto
            {
                Scope = scope,
                ScopeId = (tenantId ?? siteId ?? agentId)?.ToString(),
                Window = FormatWindow(window),
                Kpis = new P2pKpisDto(),
                // Sem snapshots na janela: não há dados (e não "saudável com zeros").
                // O frontend usa isso + lastTelemetryAtUtc para exibir empty state.
                Health = "nodata",
                UpdatedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        var bytesServedDelta = deltas.Sum(d => d.BytesServedDelta);
        var bytesDownloadedDelta = deltas.Sum(d => d.BytesDownloadedDelta);
        var startedDelta = deltas.Sum(d => d.ReplicationsStartedDelta);
        var succeededDelta = deltas.Sum(d => d.ReplicationsSucceededDelta);
        var preloadSkippedDelta = deltas.Sum(d => d.PreloadSkippedFinalStateDelta);

        var activeAgents = deltas.Count;
        // Sem replicações na janela não há taxa observável: 0 (o frontend mostra
        // "—" quando ReplicationsStartedDelta == 0). Antes devolvia 1.0 (100%).
        var successRate = startedDelta > 0 ? (double)succeededDelta / startedDelta : 0.0;
        var queueAvg = deltas.Average(d => d.QueueAvg);
        var queuePressure = Math.Min(queueAvg / 1000.0, 1.0);

        // Artifacts com peers na janela TTL 2h
        var presenceCutoff = DateTime.UtcNow.AddHours(-2);
        var artifactsWithPeers = await BuildArtifactPresenceQuery(scope, tenantId, siteId, agentId)
            .Where(p => p.LastSeenAt >= presenceCutoff)
            .Select(p => p.ArtifactId)
            .Distinct()
            .CountAsync(ct);

        var activeSeeders = deltas.Count(d => d.MaxSelectedSeeds > 0);
        // Sem replicações na janela não há taxa de falha observável: não penalizar
        // a saúde da rede por ausência de atividade (antes: failureRate=1.0 → "critical").
        var observedFailureRate = startedDelta > 0 ? 1.0 - successRate : 0.0;
        var health = DetermineHealth(observedFailureRate, queuePressure);

        return new P2pOverviewDto
        {
            Scope = scope,
            ScopeId = (tenantId ?? siteId ?? agentId)?.ToString(),
            Window = FormatWindow(window),
            Kpis = new P2pKpisDto
            {
                ActiveAgents = activeAgents,
                ActiveSeeders = activeSeeders,
                // Percentual (0..100), consistente com DashboardService.CalculateSuccessRate.
                ReplicationSuccessRate = Math.Round(successRate * 100, 2),
                BytesServedDelta = bytesServedDelta,
                BytesDownloadedDelta = bytesDownloadedDelta,
                ReplicationsStartedDelta = startedDelta,
                ReplicationsSucceededDelta = succeededDelta,
                PreloadSkippedFinalStateDelta = preloadSkippedDelta,
                QueuePressure = Math.Round(queuePressure, 4),
                ArtifactsWithPeers = artifactsWithPeers,
                LastTelemetryAtUtc = deltas.Max(d => d.LastReceivedAt).ToString("O")
            },
            Health = health,
            UpdatedAtUtc = DateTime.UtcNow.ToString("O")
        };
    }

    public async Task<P2pTimeseriesDto> GetTimeseriesAsync(
        string scope,
        Guid? tenantId,
        Guid? siteId,
        Guid? agentId,
        string metric,
        DateTime from,
        DateTime to,
        TimeSpan interval,
        CancellationToken ct = default)
    {
        // Consistente com overview/ranking/retenção: janela pelo received_at
        // (relógio do servidor). Antes usava collected_at (relógio do agente),
        // o que podia divergir e não usava o índice ix_p2p_telemetry_received_at.
        var query = _db.P2pAgentTelemetries
            .AsNoTracking()
            .Where(t => t.ReceivedAt >= from && t.ReceivedAt <= to);

        query = scope switch
        {
            "tenant" when tenantId.HasValue => query.Where(t => t.ClientId == tenantId.Value),
            "site" when siteId.HasValue => query.Where(t => t.SiteId == siteId.Value),
            "agent" when agentId.HasValue => query.Where(t => t.AgentId == agentId.Value),
            _ => query
        };

        var snapshots = await query
            .OrderBy(t => t.ReceivedAt)
            .ToListAsync(ct);

        var points = BuildTimeseries(snapshots, metric, from, to, interval);
        var values = points.Select(p => p.Value).ToList();

        return new P2pTimeseriesDto
        {
            Metric = metric,
            Unit = GetMetricUnit(metric),
            Points = points,
            Summary = values.Count == 0 ? new P2pTimeseriesSummary() : new P2pTimeseriesSummary
            {
                Min = values.Min(),
                Max = values.Max(),
                Avg = values.Average(),
                P95 = Percentile(values, 0.95),
                Total = values.Sum()
            }
        };
    }

    public async Task<(List<P2pDistributionStatusItem> Items, int Total)> GetArtifactDistributionPageOpsAsync(
        string scope,
        Guid? tenantId,
        Guid? siteId,
        Guid? artifactId,
        string? cursor,
        int limit,
        CancellationToken ct = default)
    {
        return await QueryDistributionByScope(
            siteId: scope == "site" ? siteId : null,
            clientId: scope == "tenant" ? tenantId : null,
            global: scope == "global",
            artifactId: artifactId,
            limit: limit,
            offset: 0,
            cursor: cursor,
            ct: ct);
    }

    public async Task<List<P2pAgentRankingItem>> GetAgentRankingAsync(
        string scope,
        Guid? tenantId,
        Guid? siteId,
        TimeSpan window,
        string sortBy,
        CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - window;

        // Mesma agregação reset-aware do overview (contadores zeram no restart do agent).
        // Escopo sem tenant/site informado cai em "global" explícito: passar "agent"
        // com agentId nulo acabava sem filtro nenhum (ranking global silencioso).
        var effectiveScope = scope == "tenant" && tenantId.HasValue
            ? "tenant"
            : scope == "site" && siteId.HasValue ? "site" : "global";
        var deltas = await QueryAgentCounterDeltasAsync(
            effectiveScope,
            effectiveScope == "tenant" ? tenantId : null,
            effectiveScope == "site" ? siteId : null,
            null,
            cutoff,
            ct);

        if (deltas.Count == 0) return new List<P2pAgentRankingItem>();

        var items = deltas.Select(delta =>
        {
            var startedDelta = delta.ReplicationsStartedDelta;
            var succeededDelta = delta.ReplicationsSucceededDelta;
            var failedDelta = delta.ReplicationsFailedDelta;

            // Sem replicações não há taxa observável: 0 (antes: success=100%).
            var successRate = startedDelta > 0 ? (double)succeededDelta / startedDelta : 0.0;
            var failureRate = startedDelta > 0 ? (double)failedDelta / startedDelta : 0.0;
            var queueAvg = delta.QueueAvg;
            var queuePressure = Math.Min(queueAvg / 1000.0, 1.0);
            var healthScore = Math.Max(0, 100.0 - (failureRate * 40 + queuePressure * 30));

            return new P2pAgentRankingItem
            {
                AgentId = delta.AgentId.ToString(),
                SiteId = delta.SiteId.ToString(),
                ClientId = delta.ClientId.ToString(),
                HealthScore = Math.Round(healthScore, 1),
                ReplicationsStartedDelta = startedDelta,
                // Percentuais (0..100), consistentes com o overview do dashboard.
                SuccessRate = Math.Round(successRate * 100, 2),
                FailureRate = Math.Round(failureRate * 100, 2),
                BytesServedDelta = delta.BytesServedDelta,
                BytesDownloadedDelta = delta.BytesDownloadedDelta,
                ActiveReplicationsAvg = Math.Round(delta.ActiveAvg, 2),
                QueuedReplicationsAvg = Math.Round(queueAvg, 2),
                LastTelemetryAtUtc = delta.LastReceivedAt.ToString("O")
            };
        }).ToList();

        return sortBy switch
        {
            "bytesServed" => items.OrderByDescending(i => i.BytesServedDelta).ToList(),
            "failureRate" => items.OrderByDescending(i => i.FailureRate).ToList(),
            "queuePressure" => items.OrderByDescending(i => i.QueuedReplicationsAvg).ToList(),
            _ => items.OrderByDescending(i => i.HealthScore).ToList()
        };
    }

    public async Task<List<P2pSeedPlanHistoryItem>> GetSeedPlanStatusAsync(
        string scope,
        Guid? tenantId,
        Guid? siteId,
        CancellationToken ct = default)
    {
        var query = _db.P2pSeedPlans.AsNoTracking();

        if (scope == "site" && siteId.HasValue)
            query = query.Where(p => p.SiteId == siteId.Value);
        else if (scope == "tenant" && tenantId.HasValue)
            query = query.Where(p => p.ClientId == tenantId.Value);

        var plans = await query.OrderByDescending(p => p.GeneratedAt).ToListAsync(ct);

        return plans.Select(p => new P2pSeedPlanHistoryItem
        {
            SiteId = p.SiteId.ToString(),
            TotalAgents = p.TotalAgents,
            ConfiguredPercent = p.ConfiguredPercent,
            MinSeeds = p.MinSeeds,
            SelectedSeeds = p.SelectedSeeds,
            GeneratedAtUtc = p.GeneratedAt.ToString("O")
        }).ToList();
    }

    // ──────────────────────────────────────────────────────────────────────
    // VALIDAÇÃO
    // ──────────────────────────────────────────────────────────────────────

    private static List<P2pErrorDetail> ValidateTelemetryRequest(
        P2pTelemetryRequest req,
        Guid resolvedAgentId,
        Guid resolvedSiteId)
    {
        var errors = new List<P2pErrorDetail>();
        var now = DateTime.UtcNow;

        // agentId body vs token
        if (!string.IsNullOrWhiteSpace(req.AgentId)
            && !string.Equals(req.AgentId, resolvedAgentId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new P2pErrorDetail
            {
                Field = "agentId",
                Code = "AGENT_ID_MISMATCH",
                Message = "agentId no payload não corresponde ao token"
            });
        }

        // siteId body vs token
        if (!string.IsNullOrWhiteSpace(req.SiteId)
            && !string.Equals(req.SiteId, resolvedSiteId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new P2pErrorDetail
            {
                Field = "siteId",
                Code = "AGENT_ID_MISMATCH",
                Message = "siteId no payload não corresponde ao token"
            });
        }

        // collectedAtUtc obrigatório
        if (string.IsNullOrWhiteSpace(req.CollectedAtUtc))
        {
            errors.Add(new P2pErrorDetail { Field = "collectedAtUtc", Code = "INVALID_RFC3339", Message = "collectedAtUtc é obrigatório" });
        }
        else if (!DateTime.TryParse(req.CollectedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var collectedAt))
        {
            errors.Add(new P2pErrorDetail { Field = "collectedAtUtc", Code = "INVALID_RFC3339", Message = "formato inválido; esperado RFC3339 com timezone" });
        }
        else
        {
            if (collectedAt.ToUniversalTime() > now.AddMinutes(5))
                errors.Add(new P2pErrorDetail { Field = "collectedAtUtc", Code = "TIMESTAMP_IN_FUTURE", Message = "collectedAtUtc está no futuro (tolerância de +5min)" });

            if (collectedAt.ToUniversalTime() < now.AddHours(-24))
                errors.Add(new P2pErrorDetail { Field = "collectedAtUtc", Code = "TIMESTAMP_TOO_OLD", Message = "collectedAtUtc demasiado antigo (máx. 24h)" });
        }

        // metrics obrigatório
        if (req.Metrics is null)
        {
            errors.Add(new P2pErrorDetail { Field = "metrics", Code = "FIELD_REQUIRED", Message = "metrics é obrigatório" });
        }
        else
        {
            ValidateMetrics(req.Metrics, errors);
        }

        // currentSeedPlan obrigatório
        if (req.CurrentSeedPlan is null)
        {
            errors.Add(new P2pErrorDetail { Field = "currentSeedPlan", Code = "FIELD_REQUIRED", Message = "currentSeedPlan é obrigatório" });
        }
        else
        {
            ValidateSeedPlan(req.CurrentSeedPlan, errors);
        }

        // ── Validações de telemetria enriquecida ──
        if (req.Artifacts is { Count: > 500 })
        {
            errors.Add(new P2pErrorDetail
            {
                Field = "artifacts",
                Code = "TOO_MANY_ITEMS",
                Message = "artifacts excede 500 itens. O agent deve truncar antes de enviar."
            });
        }

        if (req.HostLoad is not null)
        {
            if (req.HostLoad.CpuPercent is < 0 or > 100)
                errors.Add(new P2pErrorDetail { Field = "hostLoad.cpuPercent", Code = "OUT_OF_RANGE", Message = "cpuPercent deve estar entre 0 e 100" });

            if (req.HostLoad.MemoryPercent is < 0 or > 100)
                errors.Add(new P2pErrorDetail { Field = "hostLoad.memoryPercent", Code = "OUT_OF_RANGE", Message = "memoryPercent deve estar entre 0 e 100" });

            if (req.HostLoad.DiskBusyPercent is < 0 or > 100)
                errors.Add(new P2pErrorDetail { Field = "hostLoad.diskBusyPercent", Code = "OUT_OF_RANGE", Message = "diskBusyPercent deve estar entre 0 e 100" });

            if (req.HostLoad.CpuCores < 1)
                errors.Add(new P2pErrorDetail { Field = "hostLoad.cpuCores", Code = "INVALID_VALUE", Message = "cpuCores deve ser ≥ 1" });

            if (req.HostLoad.RamGB < 0.1)
                errors.Add(new P2pErrorDetail { Field = "hostLoad.ramGB", Code = "INVALID_VALUE", Message = "ramGB deve ser ≥ 0.1" });
        }

        // KnownPeers >= ConnectedPeers (warning, não rejeição)
        if (req.KnownPeers < req.ConnectedPeers && req.ConnectedPeers > 0)
        {
            errors.Add(new P2pErrorDetail
            {
                Field = "knownPeers",
                Code = "INCONSISTENT",
                Message = $"knownPeers ({req.KnownPeers}) é menor que connectedPeers ({req.ConnectedPeers})"
            });
        }

        return errors;
    }

    private static void ValidateMetrics(P2pMetricsDto m, List<P2pErrorDetail> errors)
    {
        if (m.PublishedArtifacts < 0 || m.ReplicationsStarted < 0 || m.ReplicationsSucceeded < 0
            || m.ReplicationsFailed < 0 || m.BytesServed < 0 || m.BytesDownloaded < 0
            || m.QueuedReplications < 0 || m.ActiveReplications < 0 || m.AutoDistributionRuns < 0
            || m.CatalogRefreshRuns < 0 || m.ChunkedDownloads < 0 || m.ChunksDownloaded < 0)
        {
            errors.Add(new P2pErrorDetail { Field = "metrics", Code = "METRIC_NEGATIVE", Message = "Todos os campos de metrics devem ser ≥ 0" });
        }

        // Consistência: succeeded + failed ≤ started + 1
        if (m.ReplicationsSucceeded + m.ReplicationsFailed > m.ReplicationsStarted + 1)
        {
            errors.Add(new P2pErrorDetail
            {
                Field = "metrics",
                Code = "METRIC_INCONSISTENT",
                Message = $"replicationsSucceeded ({m.ReplicationsSucceeded}) + replicationsFailed ({m.ReplicationsFailed}) > replicationsStarted ({m.ReplicationsStarted}) + 1"
            });
        }

        if (m.ActiveReplications > 64)
            errors.Add(new P2pErrorDetail { Field = "metrics.activeReplications", Code = "METRIC_INCONSISTENT", Message = "activeReplications excede 64" });

        if (m.QueuedReplications > 1_000)
            errors.Add(new P2pErrorDetail { Field = "metrics.queuedReplications", Code = "METRIC_INCONSISTENT", Message = "queuedReplications excede 1.000" });

        if (m.BytesServed > OneTibytes)
            errors.Add(new P2pErrorDetail { Field = "metrics.bytesServed", Code = "METRIC_INCONSISTENT", Message = "bytesServed excede 1 TiB" });

        if (m.BytesDownloaded > OneTibytes)
            errors.Add(new P2pErrorDetail { Field = "metrics.bytesDownloaded", Code = "METRIC_INCONSISTENT", Message = "bytesDownloaded excede 1 TiB" });

        if (m.PublishedArtifacts > 100_000)
            errors.Add(new P2pErrorDetail { Field = "metrics.publishedArtifacts", Code = "METRIC_INCONSISTENT", Message = "publishedArtifacts excede 100.000" });

        if (m.ReplicationsStarted > 10_000_000)
            errors.Add(new P2pErrorDetail { Field = "metrics.replicationsStarted", Code = "METRIC_INCONSISTENT", Message = "replicationsStarted excede 10.000.000" });

        if (m.ReplicationsSucceeded > 10_000_000)
            errors.Add(new P2pErrorDetail { Field = "metrics.replicationsSucceeded", Code = "METRIC_INCONSISTENT", Message = "replicationsSucceeded excede 10.000.000" });

        if (m.ReplicationsFailed > 10_000_000)
            errors.Add(new P2pErrorDetail { Field = "metrics.replicationsFailed", Code = "METRIC_INCONSISTENT", Message = "replicationsFailed excede 10.000.000" });

        if (m.AutoDistributionRuns > 10_000_000)
            errors.Add(new P2pErrorDetail { Field = "metrics.autoDistributionRuns", Code = "METRIC_INCONSISTENT", Message = "autoDistributionRuns excede 10.000.000" });

        if (m.CatalogRefreshRuns > 10_000_000)
            errors.Add(new P2pErrorDetail { Field = "metrics.catalogRefreshRuns", Code = "METRIC_INCONSISTENT", Message = "catalogRefreshRuns excede 10.000.000" });

        if (m.ChunkedDownloads > 10_000_000)
            errors.Add(new P2pErrorDetail { Field = "metrics.chunkedDownloads", Code = "METRIC_INCONSISTENT", Message = "chunkedDownloads excede 10.000.000" });

        if (m.ChunksDownloaded > 10_000_000_000)
            errors.Add(new P2pErrorDetail { Field = "metrics.chunksDownloaded", Code = "METRIC_INCONSISTENT", Message = "chunksDownloaded excede 10.000.000.000" });
    }

    private static void ValidateSeedPlan(P2pSeedPlanDto plan, List<P2pErrorDetail> errors)
    {
        if (plan.TotalAgents < 0 || plan.ConfiguredPercent < 0 || plan.MinSeeds < 0 || plan.SelectedSeeds < 0)
        {
            errors.Add(new P2pErrorDetail { Field = "currentSeedPlan", Code = "METRIC_NEGATIVE", Message = "Todos os campos do seed plan devem ser ≥ 0" });
            return;
        }

        if (plan.SelectedSeeds > plan.TotalAgents && plan.TotalAgents > 0)
        {
            errors.Add(new P2pErrorDetail
            {
                Field = "currentSeedPlan",
                Code = "METRIC_INCONSISTENT",
                Message = $"selectedSeeds ({plan.SelectedSeeds}) > totalAgents ({plan.TotalAgents})"
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // HELPERS
    // ──────────────────────────────────────────────────────────────────────

    private async Task<(List<P2pDistributionStatusItem> Items, int Total)> QueryDistributionByScope(
        Guid? siteId,
        Guid? clientId,
        bool global,
        Guid? artifactId,
        int limit,
        int offset,
        string? cursor,
        CancellationToken ct)
    {
        var presenceCutoff = DateTime.UtcNow.AddHours(-2);

        var baseQuery = _db.P2pArtifactPresences
            .AsNoTracking()
            .Where(p => p.LastSeenAt >= presenceCutoff);

        if (!global)
        {
            if (siteId.HasValue)
                baseQuery = baseQuery.Where(p => p.SiteId == siteId.Value);
            else if (clientId.HasValue)
                baseQuery = baseQuery.Where(p => p.ClientId == clientId.Value);
        }

        if (artifactId.HasValue)
            baseQuery = baseQuery.Where(p => p.ArtifactId == artifactId.Value);

        // ── Fase 1: GROUP BY no banco com keyset Type A ──────────────────
        var aggQuery = baseQuery
            .GroupBy(p => new { p.ArtifactId, p.ArtifactName })
            .Select(g => new
            {
                g.Key.ArtifactId,
                g.Key.ArtifactName,
                PeerCount = g.Count(),
                LastUpdatedUtc = g.Max(p => p.LastSeenAt)
            });

        // Keyset filter (Type A: DateTime desc + Guid desc)
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (CursorPaginationHelper.TryDecodeCreatedAtCursor(cursor, out var cursorTs, out var cursorId))
            {
                aggQuery = aggQuery.Where(a =>
                    a.LastUpdatedUtc < cursorTs ||
                    (a.LastUpdatedUtc == cursorTs && a.ArtifactId.CompareTo(cursorId) < 0));
            }
        }

        int effectiveOffset = 0;
        if (string.IsNullOrWhiteSpace(cursor) && offset > 0)
            effectiveOffset = Math.Max(0, offset);

        var total = await aggQuery.CountAsync(ct);

        var pageRows = effectiveOffset > 0
            ? await aggQuery
                .OrderByDescending(a => a.LastUpdatedUtc)
                .ThenByDescending(a => a.ArtifactId)
                .Skip(effectiveOffset)
                .Take(limit)
                .ToListAsync(ct)
            : await aggQuery
                .OrderByDescending(a => a.LastUpdatedUtc)
                .ThenByDescending(a => a.ArtifactId)
                .Take(limit)
                .ToListAsync(ct);

        // ── Fase 2: AgentIds apenas para a fatia visível ──────────────────
        var artifactIds = pageRows.Select(a => a.ArtifactId).Distinct().ToList();
        Dictionary<Guid, List<string>> agentIdsMap;
        if (artifactIds.Count > 0)
        {
            var rawAgentIds = await _db.P2pArtifactPresences
                .AsNoTracking()
                .Where(p => artifactIds.Contains(p.ArtifactId) && p.LastSeenAt >= presenceCutoff)
                .Select(p => new { p.ArtifactId, p.AgentId })
                .ToListAsync(ct);

            agentIdsMap = rawAgentIds
                .GroupBy(x => x.ArtifactId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.AgentId.ToString()).ToList());
        }
        else
        {
            agentIdsMap = new();
        }

        var items = pageRows.Select(a => new P2pDistributionStatusItem
        {
            ArtifactId = a.ArtifactId,
            ArtifactName = a.ArtifactName,
            PeerCount = a.PeerCount,
            PeerAgentIds = agentIdsMap.TryGetValue(a.ArtifactId, out var ids) && ids.Count <= 500 ? ids : null,
            LastUpdatedUtc = a.LastUpdatedUtc.ToString("O")
        }).ToList();

        return (items, total);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation;

    /// <summary>Deltas cumulativos por agente na janela, já tratando reset de processo.</summary>
    internal sealed record P2pAgentCounterDelta(
        Guid AgentId,
        Guid SiteId,
        Guid ClientId,
        long BytesServedDelta,
        long BytesDownloadedDelta,
        long ReplicationsStartedDelta,
        long ReplicationsSucceededDelta,
        long ReplicationsFailedDelta,
        long PreloadSkippedFinalStateDelta,
        double QueueAvg,
        double ActiveAvg,
        int MaxSelectedSeeds,
        int SampleCount,
        DateTime LastReceivedAt);

    /// <summary>
    /// Soma os incrementos dos contadores cumulativos por agente dentro da janela.
    ///
    /// Os contadores vivem em memória no agent e ZERAM quando ele reinicia. Calcular
    /// "última amostra - primeira amostra" devolvia 0 (ou negativo, clampeado) nesse
    /// cenário — era por isso que bytes servidos/baixados apareciam como 0 B mesmo com
    /// centenas de MB transferidos. Aqui, ordenando as amostras:
    ///   - se o contador cresceu: soma a diferença;
    ///   - se caiu (restart): soma o valor atual como início de um novo segmento;
    ///   - amostra repetida (mesmo collected_at) gera diferença zero → não duplica.
    /// Um baseline (último snapshot ANTES da janela) evita contar o que foi transferido
    /// antes dela.
    ///
    /// Feito em SQL com funções de janela (LAG/DISTINCT ON) para não materializar todas
    /// as amostras. Provider PostgreSQL (único usado pelo projeto).
    /// </summary>
    private async Task<List<P2pAgentCounterDelta>> QueryAgentCounterDeltasAsync(
        string scope,
        Guid? tenantId,
        Guid? siteId,
        Guid? agentId,
        DateTime cutoff,
        CancellationToken ct)
    {
        var (scopeFilter, scopeValue) = scope switch
        {
            "tenant" when tenantId.HasValue => (" AND client_id = @scope_id", (Guid?)tenantId.Value),
            "site" when siteId.HasValue => (" AND site_id = @scope_id", (Guid?)siteId.Value),
            "agent" when agentId.HasValue => (" AND agent_id = @scope_id", (Guid?)agentId.Value),
            _ => (string.Empty, (Guid?)null)
        };

        var sql = $@"
WITH scoped AS (
    SELECT id, agent_id, site_id, client_id, received_at, collected_at,
           bytes_served, bytes_downloaded,
           replications_started, replications_succeeded, replications_failed,
           queued_replications, active_replications, plan_selected_seeds,
           preload_skipped_final_state
    FROM p2p_agent_telemetry
    WHERE received_at >= @cutoff{scopeFilter}
),
agents AS (SELECT DISTINCT agent_id FROM scoped),
baseline AS (
    SELECT DISTINCT ON (t.agent_id)
           t.agent_id, t.bytes_served, t.bytes_downloaded,
           t.replications_started, t.replications_succeeded, t.replications_failed,
           t.preload_skipped_final_state
    FROM p2p_agent_telemetry t
    JOIN agents a ON a.agent_id = t.agent_id
    WHERE t.received_at < @cutoff
    ORDER BY t.agent_id, t.collected_at DESC, t.id DESC
),
ordered AS (
    SELECT s.*,
        COALESCE(LAG(s.bytes_served) OVER w, b.bytes_served, 0) AS prev_served,
        COALESCE(LAG(s.bytes_downloaded) OVER w, b.bytes_downloaded, 0) AS prev_downloaded,
        COALESCE(LAG(s.replications_started) OVER w, b.replications_started, 0) AS prev_started,
        COALESCE(LAG(s.replications_succeeded) OVER w, b.replications_succeeded, 0) AS prev_succeeded,
        COALESCE(LAG(s.replications_failed) OVER w, b.replications_failed, 0) AS prev_failed,
        COALESCE(LAG(s.preload_skipped_final_state) OVER w, b.preload_skipped_final_state, 0) AS prev_preload
    FROM scoped s
    LEFT JOIN baseline b ON b.agent_id = s.agent_id
    -- Ordena pelo relógio do SNAPSHOT (collected_at), não pelo de chegada:
    -- replays do outbox podem chegar fora de ordem e um valor antigo depois de um
    -- novo seria interpretado como reset, contando o mesmo trafego duas vezes.
    WINDOW w AS (PARTITION BY s.agent_id ORDER BY s.collected_at, s.id)
)
SELECT agent_id,
       (array_agg(site_id))[1] AS site_id,
       (array_agg(client_id))[1] AS client_id,
       COALESCE(SUM(CASE WHEN bytes_served >= prev_served THEN bytes_served - prev_served ELSE bytes_served END), 0) AS served_delta,
       COALESCE(SUM(CASE WHEN bytes_downloaded >= prev_downloaded THEN bytes_downloaded - prev_downloaded ELSE bytes_downloaded END), 0) AS downloaded_delta,
       COALESCE(SUM(CASE WHEN replications_started >= prev_started THEN replications_started - prev_started ELSE replications_started END), 0) AS started_delta,
       COALESCE(SUM(CASE WHEN replications_succeeded >= prev_succeeded THEN replications_succeeded - prev_succeeded ELSE replications_succeeded END), 0) AS succeeded_delta,
       COALESCE(SUM(CASE WHEN replications_failed >= prev_failed THEN replications_failed - prev_failed ELSE replications_failed END), 0) AS failed_delta,
       COALESCE(SUM(CASE WHEN preload_skipped_final_state >= prev_preload THEN preload_skipped_final_state - prev_preload ELSE preload_skipped_final_state END), 0) AS preload_delta,
       COALESCE(AVG(queued_replications), 0)::float8 AS queue_avg,
       COALESCE(AVG(active_replications), 0)::float8 AS active_avg,
       COALESCE(MAX(plan_selected_seeds), 0) AS max_selected_seeds,
       COUNT(*)::int AS sample_count,
       MAX(received_at) AS last_received_at
FROM ordered
GROUP BY agent_id";

        // Providers sem funções de janela (testes/InMemory/SQLite) usam o mesmo
        // algoritmo em memória; produção (PostgreSQL) roda tudo no banco.
        if (!_db.Database.IsNpgsql())
            return await QueryAgentCounterDeltasInMemoryAsync(scope, tenantId, siteId, agentId, cutoff, ct);

        var result = new List<P2pAgentCounterDelta>();
        var connection = _db.Database.GetDbConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.TimestampTz) { Value = cutoff });
        if (scopeValue.HasValue)
            cmd.Parameters.Add(new NpgsqlParameter("scope_id", NpgsqlDbType.Uuid) { Value = scopeValue.Value });

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new P2pAgentCounterDelta(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetDouble(9),
                reader.GetDouble(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                DateTime.SpecifyKind(reader.GetDateTime(13), DateTimeKind.Utc)));
        }

        return result;
    }

    /// <summary>
    /// Fallback do cálculo de deltas para providers sem funções de janela
    /// (InMemory/SQLite usados em testes). Mantém exatamente a mesma semântica
    /// reset-aware do SQL.
    /// </summary>
    private async Task<List<P2pAgentCounterDelta>> QueryAgentCounterDeltasInMemoryAsync(
        string scope,
        Guid? tenantId,
        Guid? siteId,
        Guid? agentId,
        DateTime cutoff,
        CancellationToken ct)
    {
        var windowQuery = ApplyTelemetryScope(_db.P2pAgentTelemetries.AsNoTracking(), scope, tenantId, siteId, agentId)
            .Where(t => t.ReceivedAt >= cutoff);
        var windowRows = await windowQuery.ToListAsync(ct);
        if (windowRows.Count == 0)
            return [];

        var agentIds = windowRows.Select(t => t.AgentId).Distinct().ToList();
        var beforeRows = await _db.P2pAgentTelemetries.AsNoTracking()
            .Where(t => agentIds.Contains(t.AgentId) && t.ReceivedAt < cutoff)
            .ToListAsync(ct);

        return ComputeCounterDeltas(windowRows, beforeRows);
    }

    private static IQueryable<P2pAgentTelemetry> ApplyTelemetryScope(
        IQueryable<P2pAgentTelemetry> query,
        string scope,
        Guid? tenantId,
        Guid? siteId,
        Guid? agentId) => scope switch
        {
            "tenant" when tenantId.HasValue => query.Where(t => t.ClientId == tenantId.Value),
            "site" when siteId.HasValue => query.Where(t => t.SiteId == siteId.Value),
            "agent" when agentId.HasValue => query.Where(t => t.AgentId == agentId.Value),
            _ => query
        };

    /// <summary>
    /// Acumula os incrementos dos contadores cumulativos (mesma regra do SQL):
    /// crescimento soma a diferença; queda (restart) soma o valor atual como novo
    /// segmento; amostra repetida gera diferença zero.
    /// </summary>
    internal static List<P2pAgentCounterDelta> ComputeCounterDeltas(
        IReadOnlyList<P2pAgentTelemetry> windowRows,
        IReadOnlyList<P2pAgentTelemetry> beforeRows)
    {
        var baseline = beforeRows
            .GroupBy(t => t.AgentId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(t => t.CollectedAt).ThenBy(t => t.Id).Last());

        var result = new List<P2pAgentCounterDelta>();
        foreach (var group in windowRows.GroupBy(t => t.AgentId))
        {
            // Ordena pelo relógio do snapshot (ver comentário no SQL): replay de
            // outbox fora de ordem não pode virar "reset" e contar em dobro.
            var ordered = group.OrderBy(t => t.CollectedAt).ThenBy(t => t.Id).ToList();
            var previous = baseline.GetValueOrDefault(group.Key);

            long served = 0, downloaded = 0, started = 0, succeeded = 0, failed = 0, preload = 0;
            foreach (var current in ordered)
            {
                served += CounterIncrement(previous?.BytesServed, current.BytesServed);
                downloaded += CounterIncrement(previous?.BytesDownloaded, current.BytesDownloaded);
                started += CounterIncrement(previous?.ReplicationsStarted, current.ReplicationsStarted);
                succeeded += CounterIncrement(previous?.ReplicationsSucceeded, current.ReplicationsSucceeded);
                failed += CounterIncrement(previous?.ReplicationsFailed, current.ReplicationsFailed);
                preload += CounterIncrement(previous?.PreloadSkippedFinalState, current.PreloadSkippedFinalState);
                previous = current;
            }

            var last = ordered[^1];
            result.Add(new P2pAgentCounterDelta(
                group.Key,
                last.SiteId,
                last.ClientId,
                served,
                downloaded,
                started,
                succeeded,
                failed,
                preload,
                ordered.Average(t => (double)t.QueuedReplications),
                ordered.Average(t => (double)t.ActiveReplications),
                ordered.Max(t => t.PlanSelectedSeeds),
                ordered.Count,
                ordered.Max(t => t.ReceivedAt)));
        }

        return result;
    }

    private static long CounterIncrement(long? previous, long current)
        => previous.HasValue && current >= previous.Value ? current - previous.Value : current;

    private IQueryable<P2pArtifactPresence> BuildArtifactPresenceQuery(string scope, Guid? tenantId, Guid? siteId, Guid? agentId)
    {
        var q = _db.P2pArtifactPresences.AsNoTracking();
        return scope switch
        {
            "tenant" when tenantId.HasValue => q.Where(p => p.ClientId == tenantId.Value),
            "site" when siteId.HasValue => q.Where(p => p.SiteId == siteId.Value),
            // O escopo "agent" não filtrava a presença — o KPI contava artefatos
            // de outros agentes do mesmo site/cliente.
            "agent" when agentId.HasValue => q.Where(p => p.AgentId == agentId.Value),
            _ => q
        };
    }

    private static List<P2pTimeseriesPoint> BuildTimeseries(
        List<P2pAgentTelemetry> snapshots,
        string metric,
        DateTime from,
        DateTime to,
        TimeSpan interval)
    {
        var bucketCount = Math.Max(1, (int)Math.Ceiling((to - from) / interval));
        var totals = new double[bucketCount];
        var gaugeSums = new double[bucketCount];
        var gaugeCounts = new int[bucketCount];

        // Métricas cumulativas: o valor do bucket é o INCREMENTO desde o snapshot
        // anterior DO MESMO agente (queda = restart do agent → conta o valor atual
        // como novo segmento). Somar o valor absoluto por bucket (comportamento
        // antigo) desenhava o acumulado, não o tráfego do período. Ordena por
        // collected_at para tolerar replay de outbox fora de ordem.
        var cumulative = IsCumulativeMetric(metric);
        var previous = new Dictionary<Guid, long>();
        foreach (var snapshot in snapshots.OrderBy(s => s.CollectedAt).ThenBy(s => s.Id))
        {
            var index = (int)((snapshot.ReceivedAt - from).Ticks / interval.Ticks);
            if (index < 0) index = 0;
            if (index >= bucketCount) index = bucketCount - 1;

            if (cumulative)
            {
                var current = GetCumulativeCounter(metric, snapshot);
                previous.TryGetValue(snapshot.AgentId, out var previousValue);
                var first = !previous.ContainsKey(snapshot.AgentId);
                totals[index] += first ? current : CounterIncrement(previousValue, current);
                previous[snapshot.AgentId] = current;
                continue;
            }

            gaugeSums[index] += metric switch
            {
                "activeReplications" => snapshot.ActiveReplications,
                "queuedReplications" => snapshot.QueuedReplications,
                _ => 0
            };
            gaugeCounts[index]++;
        }

        var points = new List<P2pTimeseriesPoint>(bucketCount);
        for (var i = 0; i < bucketCount; i++)
        {
            var value = cumulative
                ? totals[i]
                : gaugeCounts[i] == 0 ? 0 : gaugeSums[i] / gaugeCounts[i];
            points.Add(new P2pTimeseriesPoint((from + interval * i).ToString("O"), value));
        }

        return points;
    }

    private static bool IsCumulativeMetric(string metric) => metric is
        "bytesServed" or "bytesDownloaded" or "replicationsStarted" or
        "replicationsSucceeded" or "replicationsFailed" or "chunkedDownloads";

    private static long GetCumulativeCounter(string metric, P2pAgentTelemetry snapshot) => metric switch
    {
        "bytesServed" => snapshot.BytesServed,
        "bytesDownloaded" => snapshot.BytesDownloaded,
        "replicationsStarted" => snapshot.ReplicationsStarted,
        "replicationsSucceeded" => snapshot.ReplicationsSucceeded,
        "replicationsFailed" => snapshot.ReplicationsFailed,
        "chunkedDownloads" => snapshot.ChunkedDownloads,
        _ => 0
    };

    private static string GetMetricUnit(string metric) => metric switch
    {
        "bytesServed" or "bytesDownloaded" => "bytes",
        _ => "count"
    };

    private static string FormatWindow(TimeSpan window) => window.TotalHours switch
    {
        <= 1 => "1h",
        <= 24 => "24h",
        <= 168 => "7d",
        <= 720 => "30d",
        _ => $"{(int)window.TotalHours}h"
    };

    private static string DetermineHealth(double failureRate, double queuePressure)
    {
        var score = failureRate * 0.6 + queuePressure * 0.4;
        return score > 0.5 ? "critical" : score > 0.2 ? "warning" : "ok";
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var idx = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(idx, sorted.Count - 1))];
    }
}
