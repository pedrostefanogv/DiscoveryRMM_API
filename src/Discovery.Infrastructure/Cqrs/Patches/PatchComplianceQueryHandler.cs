using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.Patches;

/// <summary>
/// Handler de conformidade de patches (query on-demand, sem projeção nova no banco).
///
/// Semântica por agente:
/// • pendências = inventário <c>IsPresent && IsPending</c> não negado (mesma regra
///   das visões de frota do <see cref="Repositories.PatchRepository"/>);
/// • pendências obrigatórias = pendências com <c>IsMandatory</c>;
/// • pendências falhas = pendências com <c>Status == Failed</c>;
/// • <c>Compliant</c> = scan dentro da janela (default 7 dias) e zero pendências
///   obrigatórias. Sem scan = fail-closed (não-compliant, lastScanUtc nulo);
/// • <c>Score</c> = agentes com zero pendências falhas / total de agentes × 100
///   (1 casa) — métrica LEGADA, mantida idêntica de propósito;
/// • <c>ScoreV2</c> = agentes conformes / agentes COM scan válido na janela × 100
///   (decisão D05). Agente sem scan não entra como conforme nem no denominador:
///   ele é medido por <c>ScanCoverage</c>. Sem nenhum scan válido = 0 (nada
///   medido, fail-closed — nunca 100);
/// • <c>ScanCoverage</c> = agentes com scan válido / total de agentes × 100;
/// • <c>AgentsWithoutScan</c> = total − com scan válido.
/// </summary>
public sealed class GetPatchComplianceQueryHandler(DiscoveryDbContext db)
    : IRequestHandler<GetPatchComplianceQuery, Result<PatchComplianceDto>>
{
    public async Task<Result<PatchComplianceDto>> Handle(GetPatchComplianceQuery query, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var windowDays = query.ScanWindowDays is <= 0 or > 90 ? 7 : query.ScanWindowDays;
        var scanCutoff = now.AddDays(-windowDays);

        // ── Escopo (clients/sites resolvidos em memória: tabelas de tenant são pequenas)
        var clients = await db.Clients.AsNoTracking().ToListAsync(ct);
        var sites = await db.Sites.AsNoTracking().ToListAsync(ct);

        var clientNames = clients.ToDictionary(c => c.Id, c => c.Name);
        var siteById = sites.ToDictionary(s => s.Id);

        var allowedClientIds = query.AllowedClientIds is { Count: > 0 } list ? list.ToHashSet() : null;
        var hasGlobalAccess = query.HasGlobalAccess || allowedClientIds is null;

        Guid? clientId = query.ClientId;
        var siteId = query.SiteId;

        // Restringe pelo escopo do usuário (nunca confiar só no clientId da query string).
        if (!hasGlobalAccess)
        {
            if (clientId.HasValue && !allowedClientIds!.Contains(clientId.Value))
                return Result<PatchComplianceDto>.Success(Empty(query, clientId, siteId, clientNames, now));

            if (!clientId.HasValue && siteId.HasValue && siteById.TryGetValue(siteId.Value, out var scopedSite))
                clientId = scopedSite.ClientId;

            if (!clientId.HasValue)
            {
                // Global do usuário = união dos clientes permitidos.
                var allowedSites = siteById.Values.Where(s => allowedClientIds!.Contains(s.ClientId)).ToList();
                return await BuildAsync(query, now, scanCutoff, clientNames, siteById,
                    allowedSites.Select(s => s.Id).ToHashSet(), clientId: null, siteId: null, ct);
            }
        }

        if (siteId.HasValue)
        {
            if (!siteById.TryGetValue(siteId.Value, out var site))
                return Result<PatchComplianceDto>.Success(Empty(query, clientId, siteId, clientNames, now));

            if (clientId.HasValue && site.ClientId != clientId.Value)
                return Result<PatchComplianceDto>.Success(Empty(query, clientId, siteId, clientNames, now));
        }
        else if (clientId.HasValue && !clientNames.ContainsKey(clientId.Value))
        {
            return Result<PatchComplianceDto>.Success(Empty(query, clientId, siteId, clientNames, now));
        }

        var scopeSiteIds = sites
            .Where(s => (!clientId.HasValue || s.ClientId == clientId.Value) &&
                        (!siteId.HasValue || s.Id == siteId.Value))
            .Select(s => s.Id)
            .ToHashSet();

        return await BuildAsync(query, now, scanCutoff, clientNames, siteById, scopeSiteIds, clientId, siteId, ct);
    }

    private async Task<Result<PatchComplianceDto>> BuildAsync(
        GetPatchComplianceQuery query,
        DateTime now,
        DateTime scanCutoff,
        Dictionary<Guid, string> clientNames,
        Dictionary<Guid, Site> siteById,
        HashSet<Guid> scopeSiteIds,
        Guid? clientId,
        Guid? siteId,
        CancellationToken ct)
    {
        var agents = await db.Agents.AsNoTracking()
            .Where(a => a.DeletedAt == null && scopeSiteIds.Contains(a.SiteId))
            .Select(a => new { a.Id, a.SiteId, a.DisplayName, a.Hostname })
            .ToListAsync(ct);

        var agentIds = agents.Select(a => a.Id).ToList();

        var snapshots = await db.AgentWindowsUpdateSnapshots.AsNoTracking()
            .Where(s => agentIds.Contains(s.AgentId))
            .Select(s => new { s.AgentId, s.CollectedAt })
            .ToDictionaryAsync(s => s.AgentId, s => s.CollectedAt, ct);

        var pending = await db.AgentWindowsUpdates.AsNoTracking()
            .Where(u => u.IsPresent && u.IsPending &&
                        u.ApprovalState != PatchApprovalState.Denied &&
                        agentIds.Contains(u.AgentId))
            .Select(u => new { u.AgentId, u.IsMandatory, Failed = u.Status == PatchUpdateStatus.Failed })
            .ToListAsync(ct);

        var pendingByAgent = pending
            .GroupBy(u => u.AgentId)
            .ToDictionary(g => g.Key, g => (Pending: g.Count(), Mandatory: g.Count(u => u.IsMandatory), Failed: g.Count(u => u.Failed)));

        // Métrica por agente.
        var perAgent = agents.ToDictionary(
            a => a.Id,
            a =>
            {
                var lastScanUtc = snapshots.TryGetValue(a.Id, out var collected) ? collected : null;
                var hasRecentScan = lastScanUtc.HasValue && lastScanUtc.Value >= scanCutoff;
                var counts = pendingByAgent.TryGetValue(a.Id, out var c) ? c : (Pending: 0, Mandatory: 0, Failed: 0);
                var compliant = hasRecentScan && counts.Mandatory == 0;
                return (counts.Pending, counts.Failed, Compliant: compliant, HasRecentScan: hasRecentScan,
                    LastScanUtc: (DateTime?)lastScanUtc);
            });

        // ── Rows ──
        PatchComplianceRowDto Row(Guid clientKey, string clientName, Guid? siteKey, string? siteName, IReadOnlyCollection<Guid> keys)
        {
            var totals = Aggregate(keys);
            return new PatchComplianceRowDto(clientKey, clientName, siteKey, siteName,
                totals.Agents, totals.Compliant, totals.Score, totals.Pending, totals.Failed, totals.LastScanUtc,
                totals.ScoreV2, totals.ScanCoverage, totals.AgentsWithoutScan);
        }

        (int Agents, int Compliant, int Pending, int Failed, double Score, DateTime? LastScanUtc,
            double ScoreV2, double ScanCoverage, int AgentsWithoutScan) Aggregate(IReadOnlyCollection<Guid> keys)
        {
            var selected = keys;
            var agentsCount = selected.Count;
            var compliantCount = selected.Count(k => perAgent[k].Compliant);
            var pendingCount = selected.Sum(k => perAgent[k].Pending);
            var failedCount = selected.Sum(k => perAgent[k].Failed);

            // ── Métrica legada (D05: não alterar) ──
            // Agente sem scan entra no denominador e, na prática, "passa" no
            // score antigo se não tiver pendência falha conhecida. Mantido por
            // compatibilidade com dashboards/relatórios existentes.
            var noFailed = selected.Count(k => perAgent[k].Failed == 0);
            var score = agentsCount == 0 ? 100.0 : Math.Round(noFailed * 100.0 / agentsCount, 1);

            // ── ScoreV2 + cobertura (D05) ──
            // ScoreV2 mede conformidade APENAS entre agentes com scan válido na
            // janela: sem scan não há evidência de conformidade, então o agente
            // fica fora do numerador E do denominador (quem fala dele é a
            // cobertura). Sem nenhum scan válido o score é 0 — nada medido não
            // pode virar "100% conforme" (fail-closed).
            var withScanCount = selected.Count(k => perAgent[k].HasRecentScan);
            var withoutScanCount = agentsCount - withScanCount;
            var scoreV2 = withScanCount == 0
                ? 0.0
                : Math.Round(compliantCount * 100.0 / withScanCount, 1);
            var scanCoverage = agentsCount == 0
                ? 0.0
                : Math.Round(withScanCount * 100.0 / agentsCount, 1);

            var lastScanUtc = selected.Select(k => perAgent[k].LastScanUtc)
                .Where(d => d.HasValue).Select(d => d!.Value)
                .DefaultIfEmpty().Max();
            return (agentsCount, compliantCount, pendingCount, failedCount, score,
                lastScanUtc == default ? null : lastScanUtc, scoreV2, scanCoverage, withoutScanCount);
        }

        var rows = new List<PatchComplianceRowDto>();
        if (siteId.HasValue)
        {
            // Row por agente (drill-down de um site).
            foreach (var agent in agents.OrderBy(a => a.Hostname, StringComparer.OrdinalIgnoreCase))
            {
                var site = siteById[agent.SiteId];
                var clientName = clientNames.GetValueOrDefault(site.ClientId, string.Empty);
                var agentMetrics = Aggregate([agent.Id]);
                rows.Add(new PatchComplianceRowDto(site.ClientId, clientName, site.Id, site.Name,
                    agentMetrics.Agents, agentMetrics.Compliant, agentMetrics.Score, agentMetrics.Pending,
                    agentMetrics.Failed, agentMetrics.LastScanUtc, agentMetrics.ScoreV2,
                    agentMetrics.ScanCoverage, agentMetrics.AgentsWithoutScan));
            }
        }
        else
        {
            // Row por site do escopo (client ou global).
            foreach (var site in scopeSiteIds.Select(id => siteById[id]).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                var siteAgents = agents.Where(a => a.SiteId == site.Id).Select(a => a.Id).ToList();
                rows.Add(Row(site.ClientId, clientNames.GetValueOrDefault(site.ClientId, string.Empty), site.Id, site.Name, siteAgents));
            }
        }

        // ── Totals (sempre sobre o escopo inteiro) ──
        var allAgentIds = agents.Select(a => a.Id).ToList();
        var totals = Aggregate(allAgentIds);

        string? scopeClientName = null;
        if (clientId.HasValue)
            scopeClientName = clientNames.GetValueOrDefault(clientId.Value);
        else if (siteId.HasValue && siteById.TryGetValue(siteId.Value, out var scopeSite))
            scopeClientName = clientNames.GetValueOrDefault(scopeSite.ClientId); // nome do client dono do site

        var dto = new PatchComplianceDto(
            now,
            new PatchComplianceScopeDto(clientId, scopeClientName, siteId),
            new PatchComplianceTotalsDto(totals.Agents, totals.Compliant, totals.Score, totals.Pending, totals.Failed,
                totals.ScoreV2, totals.ScanCoverage, totals.AgentsWithoutScan),
            rows);

        return Result<PatchComplianceDto>.Success(dto);
    }

    private PatchComplianceDto Empty(
        GetPatchComplianceQuery query,
        Guid? clientId,
        Guid? siteId,
        Dictionary<Guid, string> clientNames,
        DateTime now) => new(
        now,
        new PatchComplianceScopeDto(clientId, clientId.HasValue ? clientNames.GetValueOrDefault(clientId.Value) : null, siteId),
        // Escopo vazio: nada medido → cobertura/scoreV2 zero (fail-closed); o
        // score legado mantém o 100 histórico do handler.
        new PatchComplianceTotalsDto(0, 0, 100, 0, 0, 0, 0, 0),
        []);
}
