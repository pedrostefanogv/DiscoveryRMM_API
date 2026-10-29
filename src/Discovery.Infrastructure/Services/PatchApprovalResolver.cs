using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Resolve o estado efetivo de aprovação de cada atualização de um agente:
///
/// 1. Regras explícitas (patch_approvals) por escopo Agent → Site → Client →
///    Global; a mais específica vence e, em empate, Deny vence Approve.
/// 2. Sem regra explícita, a política aplicável (Site → Client → Global) pode
///    auto-aprovar por severidade/classificação.
/// 3. Caso contrário: Unknown ("aguardando aprovação").
///
/// O resultado é denormalizado em approval_state/approval_source/status para
/// que filtros e contadores de frota não precisem de joins caros.
/// </summary>
public sealed class PatchApprovalResolver(DiscoveryDbContext db) : IPatchApprovalResolver
{
    public async Task<int> RecomputeForAgentAsync(Guid agentId, CancellationToken ct = default)
    {
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(item => item.Id == agentId, ct);
        if (agent is null)
            return 0;

        var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(item => item.Id == agent.SiteId, ct);
        if (site is null)
            return 0;

        var clientId = site.ClientId;

        var approvals = await db.PatchApprovals
            .AsNoTracking()
            .Where(rule =>
                rule.ScopeType == PatchApprovalScopeType.Global ||
                (rule.ScopeType == PatchApprovalScopeType.Client && rule.ClientId == clientId) ||
                (rule.ScopeType == PatchApprovalScopeType.Site && rule.SiteId == site.Id) ||
                (rule.ScopeType == PatchApprovalScopeType.Agent && rule.AgentId == agentId))
            .ToListAsync(ct);

        var policies = await db.PatchPolicies
            .AsNoTracking()
            .Where(policy =>
                policy.ScopeType == PatchApprovalScopeType.Global ||
                (policy.ScopeType == PatchApprovalScopeType.Client && policy.ClientId == clientId) ||
                (policy.ScopeType == PatchApprovalScopeType.Site && policy.SiteId == site.Id))
            .ToListAsync(ct);

        var effectivePolicy = policies
            .OrderByDescending(policy => (int)policy.ScopeType)
            .FirstOrDefault();

        var rows = await db.AgentWindowsUpdates
            .Where(update => update.AgentId == agentId && update.IsPresent)
            .ToListAsync(ct);

        // WU-21/D24: apps de terceiros só são auto-aprovados se a versão estiver
        // no catálogo curado e ativa. Carrega uma vez por agente.
        var catalogKeys = await LoadActiveCatalogKeysAsync(rows, ct);

        return await ApplyAsync(agentId, rows, approvals, effectivePolicy, catalogKeys, ct);
    }

    public async Task<int> RecomputeForGroupKeyAsync(string groupKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
            return 0;

        var agentIds = await db.AgentWindowsUpdates
            .AsNoTracking()
            .Where(update => update.GroupKey == groupKey && update.IsPresent)
            .Select(update => update.AgentId)
            .Distinct()
            .ToListAsync(ct);

        var total = 0;
        foreach (var agentId in agentIds)
            total += await RecomputeForAgentAsync(agentId, ct);
        return total;
    }

    /// <summary>
    /// Recalcula o escopo. Mudanças de política afetam apenas agentes COM
    /// pendências — varrer a frota inteira na hora do save estourava o tempo de
    /// resposta. O job diário de manutenção reconcilia o restante.
    /// </summary>
    public async Task<int> RecomputeForScopeAsync(
        PatchApprovalScopeType scopeType,
        Guid? scopeId,
        CancellationToken ct = default)
    {
        Guid? clientId = null;
        Guid? siteId = null;
        Guid? agentId = null;

        switch (scopeType)
        {
            case PatchApprovalScopeType.Agent:
                agentId = scopeId;
                break;
            case PatchApprovalScopeType.Site:
                siteId = scopeId;
                break;
            case PatchApprovalScopeType.Client:
                clientId = scopeId;
                break;
            case PatchApprovalScopeType.Global:
                break;
        }

        if (scopeType == PatchApprovalScopeType.Agent)
            return agentId.HasValue ? await RecomputeForAgentAsync(agentId.Value, ct) : 0;

        const int maxAgentsPerRun = 1000;
        // Restringe ao escopo via o mesmo join de frota (inclui filtro de agente
        // deletado e client/site).
        var scopedAgentIds = await db.AgentWindowsUpdates
            .AsNoTracking()
            .Where(update => update.IsPresent && update.IsPending)
            .Join(db.Agents.AsNoTracking(), update => update.AgentId, agent => agent.Id,
                (update, agent) => new { update, agent })
            .Join(db.Sites.AsNoTracking(), pair => pair.agent.SiteId, site => site.Id,
                (pair, site) => new { pair.update, pair.agent, site })
            .Where(pair => pair.agent.DeletedAt == null &&
                           (clientId == null || pair.site.ClientId == clientId) &&
                           (siteId == null || pair.site.Id == siteId))
            .Select(pair => pair.update.AgentId)
            .Distinct()
            .Take(maxAgentsPerRun)
            .ToListAsync(ct);

        var total = 0;
        foreach (var id in scopedAgentIds)
            total += await RecomputeForAgentAsync(id, ct);
        return total;
    }

    public async Task RefreshSnapshotCountsAsync(Guid agentId, CancellationToken ct = default)
    {
        var counts = await db.AgentWindowsUpdates
            .AsNoTracking()
            .Where(update => update.AgentId == agentId && update.IsPresent)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Pending = group.Count(update => update.IsPending),
                Installed = group.Count(update => update.IsInstalled),
                Denied = group.Count(update => update.IsPending &&
                    update.ApprovalState == PatchApprovalState.Denied)
            })
            .FirstOrDefaultAsync(ct);

        var snapshot = await db.AgentWindowsUpdateSnapshots
            .FirstOrDefaultAsync(item => item.AgentId == agentId, ct);
        if (snapshot is null)
        {
            snapshot = new AgentWindowsUpdateSnapshot { AgentId = agentId };
            db.AgentWindowsUpdateSnapshots.Add(snapshot);
        }

        snapshot.PendingCount = counts?.Pending ?? 0;
        snapshot.InstalledCount = counts?.Installed ?? 0;
        snapshot.DeniedCount = counts?.Denied ?? 0;
        snapshot.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Chaves "source:packageId" ativas no catálogo curado (WU-21).</summary>
    private async Task<IReadOnlySet<string>> LoadActiveCatalogKeysAsync(
        IReadOnlyList<AgentWindowsUpdate> rows,
        CancellationToken ct)
    {
        var sources = rows
            .Where(row => PatchSourcePolicy.IsThirdPartySource(row.Source))
            .Select(row => (row.Source ?? string.Empty).Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        if (sources.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var entries = await db.AppCatalogEntries
            .AsNoTracking()
            .Where(entry => entry.Status == "active" && sources.Contains(entry.Source))
            .Select(entry => new { entry.Source, entry.PackageId })
            .ToListAsync(ct);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            keys.Add((entry.Source ?? string.Empty).ToLowerInvariant() + ":" + (entry.PackageId ?? string.Empty).ToLowerInvariant());
        return keys;
    }

    private async Task<int> ApplyAsync(
        Guid agentId,
        IReadOnlyList<AgentWindowsUpdate> rows,
        IReadOnlyList<PatchApproval> approvals,
        PatchPolicy? policy,
        IReadOnlySet<string> catalogKeys,
        CancellationToken ct)
    {
        var changed = 0;
        var now = DateTime.UtcNow;

        foreach (var row in rows)
        {
            var (state, source) = ResolveApproval(row, approvals, policy, now, catalogKeys);
            var status = ResolveStatus(row, state);

            if (row.ApprovalState == state && row.ApprovalSource == source && row.Status == status)
                continue;

            row.ApprovalState = state;
            row.ApprovalSource = source;
            row.Status = status;
            row.UpdatedAt = now;
            changed++;
        }

        if (changed > 0)
            await db.SaveChangesAsync(ct);
        else
            return 0;

        await RefreshSnapshotCountsAsync(agentId, ct);
        return changed;
    }

    private static (PatchApprovalState State, string? Source) ResolveApproval(
        AgentWindowsUpdate update,
        IReadOnlyList<PatchApproval> approvals,
        PatchPolicy? policy,
        DateTime now,
        IReadOnlySet<string> catalogKeys)
    {
        var candidates = approvals
            .Where(rule => string.Equals(rule.UpdateKey, update.GroupKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(rule => (int)rule.ScopeType)
            .ThenByDescending(rule => rule.UpdatedAt)
            .ThenBy(rule => rule.Action == PatchApprovalActionType.Deny ? 0 : 1)
            .ToList();

        var winner = candidates.FirstOrDefault();
        if (winner is not null)
        {
            return winner.Action == PatchApprovalActionType.Deny
                ? (PatchApprovalState.Denied, "rule")
                : (PatchApprovalState.Approved, "rule");
        }

        // Auto-aprovação por política passa antes pelo gate de deferral por
        // idade de publicação (D09). Regras explícitas acima NÃO são afetadas:
        // aprovação manual do operador vence o deferral por decisão de produto.
        if (policy is not null && AutoApproveMatches(update, policy, catalogKeys))
        {
            // Deferral por idade só se aplica a update de OS: apps de terceiros
            // raramente têm data de publicação no inventário, e aplicar o gate
            // fail-closed (D09) os reteria para sempre. Para terceiros, o
            // controle é o duplo consentimento (D22) — já checado acima.
            if (!PatchSourcePolicy.IsThirdPartySource(update.Source) &&
                !IsDeferralSatisfied(update, policy, now))
                return (PatchApprovalState.Unknown, null);

            return (PatchApprovalState.AutoApproved, "policy");
        }

        return (PatchApprovalState.Unknown, null);
    }

    /// <summary>
    /// Gate de deferral por idade de publicação (decisão D09, fail-closed).
    ///
    /// Com <c>DeferralDays &gt; 0</c>, o update só pode ser AUTO-aprovado pela
    /// política depois de <c>PublishedAt + DeferralDays</c>. Antes disso ele
    /// permanece Unknown ("aguardando aprovação"): a frota segura o patch recém
    /// publicado até o mercado sinalizar estabilidade, e o operador ainda pode
    /// aprovar manualmente se quiser antecipar.
    ///
    /// FAIL-CLOSED: sem <c>PublishedAt</c> não há como provar a idade do update,
    /// então ele NÃO é auto-aprovado (retorna false). Preferimos reter demais a
    /// aprovar pela política um patch de idade desconhecida.
    ///
    /// Aprovação manual explícita (linha em <c>patch_approvals</c> com
    /// <c>Action = Approve</c>) continua vencendo o deferral: esse caminho é
    /// resolvido antes, no bloco de regras explícitas de <see cref="ResolveApproval"/>,
    /// porque o operador decidiu conscientemente sobre aquele update.
    ///
    /// <c>DeferralDays &lt;= 0</c> = sem deferral (comportamento anterior).
    /// </summary>
    private static bool IsDeferralSatisfied(AgentWindowsUpdate update, PatchPolicy policy, DateTime now)
    {
        if (policy.DeferralDays <= 0)
            return true;

        if (!update.PublishedAt.HasValue)
            return false;

        return update.PublishedAt.Value.AddDays(policy.DeferralDays) <= now;
    }

    private static bool AutoApproveMatches(
        AgentWindowsUpdate update,
        PatchPolicy policy,
        IReadOnlySet<string> catalogKeys)
    {
        // Consentimento de ORIGEM (WU-19/WU-20, D22): app de terceiros só é
        // auto-aprovado com duplo consentimento; update de OS respeita "os".
        // Aprovação MANUAL continua possível para qualquer origem (operador
        // decide caso a caso), mas sem consentimento não há automação.
        if (PatchSourcePolicy.IsThirdPartySource(update.Source))
        {
            if (!PatchSourcePolicy.ThirdPartyConsented(policy.Sources, policy.AllowThirdPartyApps))
                return false;
            // WU-21/D24: só o caminho de DOWNLOAD DIRETO (msi) exige versão curada
            // e ativa — ali hash/assinatura vêm do catálogo. winget/choco são
            // gerenciadores de pacote e validam o próprio feed.
            if (PatchSourcePolicy.RequiresCuratedCatalog(update.Source))
            {
                var appId = Discovery.Infrastructure.Cqrs.Patches.PatchCommandPayloads.ExtractAppId(update.UpdateKey);
                var key = (update.Source ?? string.Empty).Trim().ToLowerInvariant() + ":" +
                          appId.Trim().ToLowerInvariant();
                if (!catalogKeys.Contains(key))
                    return false;
            }
        }
        else if (!PatchSourcePolicy.AllowsOs(policy.Sources))
        {
            return false;
        }

        var severities = PatchJson.ParseStringArray(policy.AutoApproveSeverities);
        if (severities.Count > 0 && !string.IsNullOrWhiteSpace(update.Severity) &&
            severities.Any(value => string.Equals(value, update.Severity, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var classifications = PatchJson.ParseStringArray(policy.AutoApproveClassifications);
        if (classifications.Count > 0 && !string.IsNullOrWhiteSpace(update.Classification) &&
            classifications.Any(value =>
                string.Equals(value, update.Classification, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static PatchUpdateStatus ResolveStatus(AgentWindowsUpdate update, PatchApprovalState state)
    {
        if (update.IsInstalled)
            return PatchUpdateStatus.Installed;

        if (update.Status == PatchUpdateStatus.Installing && state != PatchApprovalState.Denied)
            return PatchUpdateStatus.Installing;

        // Falha recente não pode ser "apagada" pelo recálculo de aprovação: o
        // operador precisa ver o erro até o próximo scan/retentativa.
        if (update.Status == PatchUpdateStatus.Failed && state != PatchApprovalState.Denied)
            return PatchUpdateStatus.Failed;

        return state switch
        {
            PatchApprovalState.Denied => PatchUpdateStatus.Denied,
            PatchApprovalState.Approved or PatchApprovalState.AutoApproved => PatchUpdateStatus.Approved,
            _ => PatchUpdateStatus.Pending
        };
    }

}
