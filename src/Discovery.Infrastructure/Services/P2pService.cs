using System.Text.RegularExpressions;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

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

    private async Task<P2pSeedPlan> RecalculateSeedPlanAsync(Guid siteId, CancellationToken ct)
    {
        var site = await _siteRepo.GetByIdAsync(siteId);
        var clientId = site?.ClientId ?? Guid.Empty;

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
            PlanTotalAgents = plan.TotalAgents,
            PlanConfiguredPercent = plan.ConfiguredPercent,
            PlanMinSeeds = plan.MinSeeds,
            PlanSelectedSeeds = plan.SelectedSeeds,
        };

        _db.P2pAgentTelemetries.Add(snapshot);
        await _db.SaveChangesAsync(ct);

        return errors;
    }

    // ──────────────────────────────────────────────────────────────────────
    // DISTRIBUTION STATUS (agent endpoint)
    // ──────────────────────────────────────────────────────────────────────

    public async Task<(List<P2pDistributionStatusItem> Items, int Total)> GetDistributionStatusAsync(
        Guid agentId,
        string? artifactId,
        int limit,
        int offset,
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
            offset: offset,
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

        var query = _db.P2pAgentTelemetries
            .AsNoTracking()
            .Where(t => t.ReceivedAt >= cutoff);

        query = scope switch
        {
            "tenant" when tenantId.HasValue => query.Where(t => t.ClientId == tenantId.Value),
            "site" when siteId.HasValue => query.Where(t => t.SiteId == siteId.Value),
            "agent" when agentId.HasValue => query.Where(t => t.AgentId == agentId.Value),
            _ => query // global
        };

        var snapshots = await query
            .OrderByDescending(t => t.CollectedAt)
            .ToListAsync(ct);

        if (snapshots.Count == 0)
        {
            return new P2pOverviewDto
            {
                Scope = scope,
                ScopeId = (tenantId ?? siteId ?? agentId)?.ToString(),
                Window = FormatWindow(window),
                Kpis = new P2pKpisDto(),
                Health = "ok",
                UpdatedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        // Último snapshot por agente para calcular estado corrente
        var latestByAgent = snapshots
            .GroupBy(t => t.AgentId)
            .Select(g => g.First())
            .ToList();

        // Primeiro snapshot por agente no período (para calcular delta)
        var firstByAgent = snapshots
            .GroupBy(t => t.AgentId)
            .Select(g => g.Last())
            .ToList();

        long bytesServedDelta = 0, bytesDownloadedDelta = 0;
        long startedDelta = 0, succeededDelta = 0;
        foreach (var latest in latestByAgent)
        {
            var first = firstByAgent.FirstOrDefault(f => f.AgentId == latest.AgentId);
            if (first is null) continue;
            bytesServedDelta += Math.Max(0, latest.BytesServed - first.BytesServed);
            bytesDownloadedDelta += Math.Max(0, latest.BytesDownloaded - first.BytesDownloaded);
            startedDelta += Math.Max(0, latest.ReplicationsStarted - first.ReplicationsStarted);
            succeededDelta += Math.Max(0, latest.ReplicationsSucceeded - first.ReplicationsSucceeded);
        }

        var activeAgents = latestByAgent.Count;
        var successRate = startedDelta > 0 ? (double)succeededDelta / startedDelta : 1.0;
        var queueAvg = latestByAgent.Average(s => (double)s.QueuedReplications);
        var queuePressure = Math.Min(queueAvg / 1000.0, 1.0);

        // Artifacts com peers na janela TTL 2h
        var presenceCutoff = DateTime.UtcNow.AddHours(-2);
        var artifactsWithPeers = await BuildArtifactPresenceQuery(scope, tenantId, siteId)
            .Where(p => p.LastSeenAt >= presenceCutoff)
            .Select(p => p.ArtifactId)
            .Distinct()
            .CountAsync(ct);

        var activeSeeders = latestByAgent.Count(s => s.PlanSelectedSeeds > 0);
        var health = DetermineHealth(1.0 - successRate, queuePressure);

        return new P2pOverviewDto
        {
            Scope = scope,
            ScopeId = (tenantId ?? siteId ?? agentId)?.ToString(),
            Window = FormatWindow(window),
            Kpis = new P2pKpisDto
            {
                ActiveAgents = activeAgents,
                ActiveSeeders = activeSeeders,
                ReplicationSuccessRate = Math.Round(successRate, 4),
                BytesServedDelta = bytesServedDelta,
                BytesDownloadedDelta = bytesDownloadedDelta,
                QueuePressure = Math.Round(queuePressure, 4),
                ArtifactsWithPeers = artifactsWithPeers,
                LastTelemetryAtUtc = snapshots.Max(s => s.ReceivedAt).ToString("O")
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
        var query = _db.P2pAgentTelemetries
            .AsNoTracking()
            .Where(t => t.CollectedAt >= from && t.CollectedAt <= to);

        query = scope switch
        {
            "tenant" when tenantId.HasValue => query.Where(t => t.ClientId == tenantId.Value),
            "site" when siteId.HasValue => query.Where(t => t.SiteId == siteId.Value),
            "agent" when agentId.HasValue => query.Where(t => t.AgentId == agentId.Value),
            _ => query
        };

        var snapshots = await query
            .OrderBy(t => t.CollectedAt)
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

    public async Task<(List<P2pDistributionStatusItem> Items, int Total)> GetArtifactDistributionOpsAsync(
        string scope,
        Guid? tenantId,
        Guid? siteId,
        string? artifactId,
        int limit,
        int offset,
        CancellationToken ct = default)
    {
        return await QueryDistributionByScope(
            siteId: scope == "site" ? siteId : null,
            clientId: scope == "tenant" ? tenantId : null,
            global: scope == "global",
            artifactId: artifactId,
            limit: limit,
            offset: offset,
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

        var query = _db.P2pAgentTelemetries
            .AsNoTracking()
            .Where(t => t.ReceivedAt >= cutoff);

        if (scope == "tenant" && tenantId.HasValue)
            query = query.Where(t => t.ClientId == tenantId.Value);
        else if (scope == "site" && siteId.HasValue)
            query = query.Where(t => t.SiteId == siteId.Value);

        var snapshots = await query.OrderBy(t => t.CollectedAt).ToListAsync(ct);
        if (snapshots.Count == 0) return new List<P2pAgentRankingItem>();

        var grouped = snapshots.GroupBy(t => t.AgentId).ToList();
        var items = new List<P2pAgentRankingItem>();

        foreach (var g in grouped)
        {
            var ordered = g.OrderBy(s => s.CollectedAt).ToList();
            var first = ordered.First();
            var last = ordered.Last();

            var startedDelta = Math.Max(0, last.ReplicationsStarted - first.ReplicationsStarted);
            var succeededDelta = Math.Max(0, last.ReplicationsSucceeded - first.ReplicationsSucceeded);
            var failedDelta = Math.Max(0, last.ReplicationsFailed - first.ReplicationsFailed);

            var successRate = startedDelta > 0 ? (double)succeededDelta / startedDelta : 1.0;
            var failureRate = startedDelta > 0 ? (double)failedDelta / startedDelta : 0.0;
            var queueAvg = ordered.Average(s => (double)s.QueuedReplications);
            var queuePressure = Math.Min(queueAvg / 1000.0, 1.0);
            var healthScore = Math.Max(0, 100.0 - (failureRate * 40 + queuePressure * 30));

            items.Add(new P2pAgentRankingItem
            {
                AgentId = first.AgentId.ToString(),
                SiteId = first.SiteId.ToString(),
                ClientId = first.ClientId.ToString(),
                HealthScore = Math.Round(healthScore, 1),
                ReplicationsStartedDelta = startedDelta,
                SuccessRate = Math.Round(successRate, 4),
                FailureRate = Math.Round(failureRate, 4),
                BytesServedDelta = Math.Max(0, last.BytesServed - first.BytesServed),
                BytesDownloadedDelta = Math.Max(0, last.BytesDownloaded - first.BytesDownloaded),
                ActiveReplicationsAvg = Math.Round(ordered.Average(s => (double)s.ActiveReplications), 2),
                QueuedReplicationsAvg = Math.Round(queueAvg, 2),
                LastTelemetryAtUtc = last.ReceivedAt.ToString("O")
            });
        }

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
        string? artifactId,
        int limit,
        int offset,
        CancellationToken ct)
    {
        var presenceCutoff = DateTime.UtcNow.AddHours(-2);

        var query = _db.P2pArtifactPresences
            .AsNoTracking()
            .Where(p => p.LastSeenAt >= presenceCutoff);

        if (!global)
        {
            if (siteId.HasValue)
                query = query.Where(p => p.SiteId == siteId.Value);
            else if (clientId.HasValue)
                query = query.Where(p => p.ClientId == clientId.Value);
        }

        if (!string.IsNullOrWhiteSpace(artifactId))
            query = query.Where(p => p.ArtifactId == artifactId);

        // Agrupar por ArtifactId
        var grouped = await query
            .GroupBy(p => new { p.ArtifactId, p.ArtifactName })
            .Select(g => new
            {
                g.Key.ArtifactId,
                g.Key.ArtifactName,
                PeerCount = g.Count(),
                AgentIds = g.Select(p => p.AgentId.ToString()).ToList(),
                LastUpdatedUtc = g.Max(p => p.LastSeenAt)
            })
            .ToListAsync(ct);

        var total = grouped.Count;
        var page = grouped
            .OrderByDescending(g => g.LastUpdatedUtc)
            .Skip(offset)
            .Take(limit)
            .ToList();

        var items = page.Select(g => new P2pDistributionStatusItem
        {
            ArtifactId = g.ArtifactId,
            ArtifactName = g.ArtifactName,
            PeerCount = g.PeerCount,
            PeerAgentIds = g.PeerCount <= 500 ? g.AgentIds : null,
            LastUpdatedUtc = g.LastUpdatedUtc.ToString("O")
        }).ToList();

        return (items, total);
    }

    private IQueryable<P2pArtifactPresence> BuildArtifactPresenceQuery(string scope, Guid? tenantId, Guid? siteId)
    {
        var q = _db.P2pArtifactPresences.AsNoTracking();
        return scope switch
        {
            "tenant" when tenantId.HasValue => q.Where(p => p.ClientId == tenantId.Value),
            "site" when siteId.HasValue => q.Where(p => p.SiteId == siteId.Value),
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
        var points = new List<P2pTimeseriesPoint>();
        var current = from;

        while (current < to)
        {
            var next = current + interval;
            var bucket = snapshots.Where(s => s.CollectedAt >= current && s.CollectedAt < next).ToList();

            double value = bucket.Count == 0 ? 0 : metric switch
            {
                "replicationsSucceeded" => bucket.Sum(s => s.ReplicationsSucceeded),
                "replicationsFailed" => bucket.Sum(s => s.ReplicationsFailed),
                "replicationsStarted" => bucket.Sum(s => s.ReplicationsStarted),
                "bytesServed" => bucket.Sum(s => s.BytesServed),
                "bytesDownloaded" => bucket.Sum(s => s.BytesDownloaded),
                "activeReplications" => bucket.Average(s => (double)s.ActiveReplications),
                "queuedReplications" => bucket.Average(s => (double)s.QueuedReplications),
                "chunkedDownloads" => bucket.Sum(s => s.ChunkedDownloads),
                _ => 0
            };

            points.Add(new P2pTimeseriesPoint(current.ToString("O"), value));
            current = next;
        }

        return points;
    }

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
