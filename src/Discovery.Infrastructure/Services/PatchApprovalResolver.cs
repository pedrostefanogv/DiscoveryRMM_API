using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
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

        return await ApplyAsync(agentId, rows, approvals, effectivePolicy, ct);
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

    private async Task<int> ApplyAsync(
        Guid agentId,
        IReadOnlyList<AgentWindowsUpdate> rows,
        IReadOnlyList<PatchApproval> approvals,
        PatchPolicy? policy,
        CancellationToken ct)
    {
        var changed = 0;
        var now = DateTime.UtcNow;

        foreach (var row in rows)
        {
            var (state, source) = ResolveApproval(row, approvals, policy);
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
        PatchPolicy? policy)
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

        if (policy is not null && AutoApproveMatches(update, policy))
            return (PatchApprovalState.AutoApproved, "policy");

        return (PatchApprovalState.Unknown, null);
    }

    private static bool AutoApproveMatches(AgentWindowsUpdate update, PatchPolicy policy)
    {
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
