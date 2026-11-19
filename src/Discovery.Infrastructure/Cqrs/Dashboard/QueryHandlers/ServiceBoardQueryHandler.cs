using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Dashboard.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.Dashboard.QueryHandlers;

/// <summary>
/// Handler do Service Board (fila unificada). Fontes:
/// • ticket — tickets ativos (<c>ClosedAt/DeletedAt</c> nulos), prioridade real do ticket;
/// • automation — execuções recentes (24h) de AutomationTaskDefinition em falha ou em andamento;
/// • patch — agentes com patches pendentes (mesma regra da visão de frota:
///   <c>IsPresent && IsPending</c> não negado), prioridade Normal;
/// • alert — alertas ativos (<c>AgentAlertDefinition</c> com status ativo),
///   prioridade = severidade real (icon: error/warning/info/success).
/// Ordenação: prioridade decrescente, depois idade decrescente; truncado em <c>Limit</c>.
/// </summary>
public sealed class GetServiceBoardQueryHandler(
    DiscoveryDbContext db,
    IScopeContext scopeContext
) : IRequestHandler<GetServiceBoardQuery, Result<ServiceBoardDto>>
{
    // Rank da prioridade para ordenação (maior = mais crítico).
    internal static int PriorityRank(string priority) => priority switch
    {
        "Critical" or "error" => 5,
        "High" or "warning" => 4,
        "Normal" or "Medium" or "info" => 3,
        "Low" or "success" => 2,
        _ => 1
    };

    public async Task<Result<ServiceBoardDto>> Handle(GetServiceBoardQuery query, CancellationToken ct)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Dashboard, ActionType.View);
        var now = DateTime.UtcNow;
        var limit = Math.Clamp(query.Limit, 1, 200);

        var clients = await db.Clients.AsNoTracking().ToListAsync(ct);
        var clientNames = clients.ToDictionary(c => c.Id, c => c.Name);
        var sites = await db.Sites.AsNoTracking().ToListAsync(ct);
        var siteById = sites.ToDictionary(s => s.Id);

        if (!access.HasGlobalAccess)
        {
            if (query.SiteId.HasValue)
            {
                var requestedSite = siteById.GetValueOrDefault(query.SiteId.Value);
                if (requestedSite is null
                    ? !access.AllowedSiteIds.Contains(query.SiteId.Value)
                    : !TenantScopePolicy.Allows(access, requestedSite.ClientId, query.SiteId.Value))
                {
                    return Result<ServiceBoardDto>.Failure(Error.Forbidden("Você não tem acesso a este site."));
                }
            }
            else if (query.ClientId.HasValue)
            {
                var hasAllowedClient = access.AllowedClientIds.Contains(query.ClientId.Value);
                var hasAllowedSite = sites.Any(s => s.ClientId == query.ClientId.Value && access.AllowedSiteIds.Contains(s.Id));
                if (!hasAllowedClient && !hasAllowedSite)
                    return Result<ServiceBoardDto>.Failure(Error.Forbidden("Você não tem acesso a este cliente."));
            }
        }

        bool ScopeAllowed(Guid? itemClientId, Guid? itemSiteId)
        {
            if (access.HasGlobalAccess)
                return true;

            if (itemSiteId.HasValue)
            {
                var resolvedClientId = itemClientId ?? siteById.GetValueOrDefault(itemSiteId.Value)?.ClientId ?? Guid.Empty;
                return TenantScopePolicy.Allows(access, resolvedClientId, itemSiteId.Value);
            }

            if (itemClientId.HasValue)
                return access.AllowedClientIds.Contains(itemClientId.Value);

            return false;
        }

        var agents = await db.Agents.AsNoTracking()
            .Where(a => a.DeletedAt == null)
            .Select(a => new { a.Id, a.SiteId, a.DisplayName, a.Hostname })
            .ToListAsync(ct);

        var siteId = query.SiteId;
        var clientId = query.ClientId;

        if (siteId.HasValue && siteById.TryGetValue(siteId.Value, out var siteOfScope))
        {
            clientId = clientId.HasValue && clientId.Value != siteOfScope.ClientId
                ? clientId // escopo inconsistente: nenhum item casa com ambos; cai nos filtros normais
                : siteOfScope.ClientId;
        }

        bool AgentInScope(Guid agentSiteId)
        {
            if (!ScopeAllowed(siteById.GetValueOrDefault(agentSiteId)?.ClientId, agentSiteId))
                return false;

            return siteId.HasValue
                ? agentSiteId == siteId.Value
                : !clientId.HasValue || (siteById.TryGetValue(agentSiteId, out var s) && s.ClientId == clientId.Value);
        }

        var items = new List<ServiceBoardItemDto>();
        int ticketCount = 0, automationCount = 0, patchCount = 0, alertCount = 0;

        // ── Tickets ativos ──
        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => t.ClosedAt == null && t.DeletedAt == null)
            .Select(t => new { t.Id, t.Title, t.Priority, t.ClientId, t.SiteId, t.CreatedAt })
            .ToListAsync(ct);

        foreach (var t in tickets)
        {
            if (!ScopeAllowed(t.ClientId, t.SiteId)) continue;
            if (siteId.HasValue && t.SiteId != siteId.Value) continue;
            if (!siteId.HasValue && clientId.HasValue && t.ClientId != clientId.Value) continue;

            ticketCount++;
            var siteKey = t.SiteId;
            items.Add(new ServiceBoardItemDto(
                "ticket", t.Id, t.Title, "open", t.Priority.ToString(),
                Math.Max(0, (now - t.CreatedAt).TotalHours),
                t.ClientId, clientNames.GetValueOrDefault(t.ClientId), siteKey,
                $"/tickets/{t.Id}"));
        }

        // ── Automações em falha/executando recentes (24h) ──
        var automationCutoff = now.AddHours(-24);
        var executions = await db.AutomationExecutionReports.AsNoTracking()
            .Where(r => r.CreatedAt >= automationCutoff &&
                        (r.Status == AutomationExecutionStatus.Failed ||
                         r.Status == AutomationExecutionStatus.Dispatched ||
                         r.Status == AutomationExecutionStatus.Acknowledged))
            .Select(r => new { r.Id, r.TaskId, r.AgentId, r.Status, r.CreatedAt })
            .ToListAsync(ct);

        var taskNames = await db.AutomationTaskDefinitions.AsNoTracking()
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct);
        var taskNameById = taskNames.ToDictionary(t => t.Id, t => t.Name);

        var agentById = agents.ToDictionary(a => a.Id);
        foreach (var r in executions)
        {
            if (!agentById.TryGetValue(r.AgentId, out var agent) || !AgentInScope(agent.SiteId)) continue;
            var site = siteById.GetValueOrDefault(agent.SiteId);

            automationCount++;
            var title = r.TaskId.HasValue ? taskNameById.GetValueOrDefault(r.TaskId.Value, "Tarefa de automação") : "Tarefa de automação";
            items.Add(new ServiceBoardItemDto(
                "automation", r.Id, title, r.Status.ToString(), r.Status == AutomationExecutionStatus.Failed ? "High" : "Normal",
                Math.Max(0, (now - r.CreatedAt).TotalHours),
                site?.ClientId, site is null ? null : clientNames.GetValueOrDefault(site.ClientId), agent.SiteId,
                r.TaskId.HasValue ? $"/automation/tasks/{r.TaskId.Value}" : "/automation/tasks"));
        }

        // ── Agentes com patches pendentes (regra da visão de frota) ──
        var pendingCounts = await db.AgentWindowsUpdates.AsNoTracking()
            .Where(u => u.IsPresent && u.IsPending && u.ApprovalState != PatchApprovalState.Denied)
            .GroupBy(u => u.AgentId)
            .Select(g => new { AgentId = g.Key, Pending = g.Count() })
            .ToListAsync(ct);

        foreach (var p in pendingCounts)
        {
            if (p.Pending <= 0) continue;
            if (!agentById.TryGetValue(p.AgentId, out var agent) || !AgentInScope(agent.SiteId)) continue;
            var site = siteById.GetValueOrDefault(agent.SiteId);
            var agentName = string.IsNullOrEmpty(agent.DisplayName) ? agent.Hostname : agent.DisplayName!;

            patchCount++;
            items.Add(new ServiceBoardItemDto(
                "patch", agent.Id, $"{p.Pending} patches pendentes — {agentName}", "pending", "Normal",
                0, // sem idade própria: pende até ser instalado
                site?.ClientId, site is null ? null : clientNames.GetValueOrDefault(site.ClientId), agent.SiteId,
                "/patches"));
        }

        // ── Alertas ativos ──
        var alerts = await db.AgentAlertDefinitions.AsNoTracking()
            .Where(a => a.Status != AlertDefinitionStatus.Cancelled && a.Status != AlertDefinitionStatus.Expired)
            .Select(a => new { a.Id, a.Title, a.Status, a.Icon, a.ScopeType, a.ScopeAgentId, a.ScopeSiteId, a.ScopeClientId, a.CreatedAt })
            .ToListAsync(ct);

        foreach (var a in alerts)
        {
            Guid? alertSiteId = a.ScopeSiteId;
            Guid? alertClientId = a.ScopeClientId;
            if (a.ScopeType == AlertScopeType.Agent && a.ScopeAgentId.HasValue && agentById.TryGetValue(a.ScopeAgentId.Value, out var scopedAgent))
            {
                alertSiteId = scopedAgent.SiteId;
                alertClientId = siteById.GetValueOrDefault(scopedAgent.SiteId)?.ClientId;
            }

            if (!ScopeAllowed(alertClientId, alertSiteId)) continue;

            // Escopo Label não tem client/site resolúveis: só entra na fila global.
            if (a.ScopeType == AlertScopeType.Label && (siteId.HasValue || clientId.HasValue)) continue;
            if (siteId.HasValue && alertSiteId != siteId.Value) continue;
            if (!siteId.HasValue && clientId.HasValue && alertClientId != clientId.Value) continue;

            alertCount++;
            items.Add(new ServiceBoardItemDto(
                "alert", a.Id, a.Title, a.Status.ToString(), a.Icon,
                Math.Max(0, (now - a.CreatedAt).TotalHours),
                alertClientId, alertClientId.HasValue ? clientNames.GetValueOrDefault(alertClientId.Value) : null, alertSiteId,
                "/alerts"));
        }

        var merged = items
            .OrderByDescending(i => PriorityRank(i.Priority))
            .ThenByDescending(i => i.AgeHours)
            .Take(limit)
            .ToList();

        var dto = new ServiceBoardDto(
            now,
            new ServiceBoardCountsDto(ticketCount, automationCount, patchCount, alertCount),
            merged);

        return Result<ServiceBoardDto>.Success(dto);
    }
}
