using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Serviço de busca universal que consulta múltiplas entidades respeitando
/// as permissões de escopo do usuário (Global → Client → Site).
/// </summary>
public class SearchService : ISearchService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly NavigationTarget[] NavigationTargets = BuildNavigationTargets();
    private const int CacheTtlSeconds = 30;
    private const int SearchTimeoutMs = 5000;
    private const int MaxResultsDefault = 10;

    private readonly DiscoveryDbContext _db;
    private readonly IScopeContext _scopeContext;
    private readonly IRedisService _redisService;

    public SearchService(
        DiscoveryDbContext db,
        IScopeContext scopeContext,
        IRedisService redisService)
    {
        _db = db;
        _scopeContext = scopeContext;
        _redisService = redisService;
    }

    public async Task<UniversalSearchResult> SearchAsync(
        Guid userId,
        string query,
        int maxResults = MaxResultsDefault,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query))
            return EmptyResult();

        query = query.Trim();
        var cacheKey = $"search:u{userId:N}:q{query.ToLowerInvariant().GetHashCode():x8}";

        // Tenta cache primeiro
        var cached = await _redisService.GetAsync(cacheKey);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            try { return JsonSerializer.Deserialize<UniversalSearchResult>(cached, JsonOptions) ?? EmptyResult(); }
            catch { await _redisService.DeleteAsync(cacheKey); }
        }

        // Define o UserId no ScopeContext
        _scopeContext.SetUserId(userId);

        // Evita concorrencia no mesmo DbContext scoped durante a resolucao de escopo.
        var agentAccess = await _scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.View);
        var clientAccess = await _scopeContext.GetAccessAsync(ResourceType.Clients, ActionType.View);
        var siteAccess = await _scopeContext.GetAccessAsync(ResourceType.Sites, ActionType.View);
        var ticketAccess = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        var reportAccess = await _scopeContext.GetAccessAsync(ResourceType.Reports, ActionType.View);
        var navigationPermissionMap = await ResolveNavigationPermissionsAsync(
            agentAccess,
            clientAccess,
            siteAccess,
            ticketAccess,
            reportAccess);

        // Executa consultas em sequencia com timeout parcial
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SearchTimeoutMs);

        var searchSteps = new Func<CancellationToken, Task<SearchResultGroup?>>[]
        {
            token => SearchNavigationAsync(query, maxResults, navigationPermissionMap, token),
            token => SearchAgentsAsync(query, agentAccess, maxResults, token),
            token => SearchClientsAsync(query, clientAccess, maxResults, token),
            token => SearchSitesAsync(query, clientAccess, siteAccess, maxResults, token),
            token => SearchTicketsAsync(query, ticketAccess, maxResults, token),
            token => SearchSoftwareAsync(query, agentAccess, maxResults, token),
            token => SearchReportTemplatesAsync(query, reportAccess, maxResults, token),
        };

        var completedGroups = new List<SearchResultGroup>();

        foreach (var searchStep in searchSteps)
        {
            try
            {
                // Evita concorrencia de consultas no mesmo DbContext scoped.
                var group = await searchStep(timeoutCts.Token);
                if (group?.Items.Count > 0)
                    completedGroups.Add(group);
            }
            catch (OperationCanceledException)
            {
                // Timeout parcial — resultados parciais são aceitáveis
                break;
            }
        }

        // Ordena grupos: coloca grupos com mais resultados primeiro
        completedGroups = completedGroups
            .OrderByDescending(g => g.Items.Count)
            .ToList();

        var totalResults = completedGroups.Sum(g => g.Items.Count);
        var result = new UniversalSearchResult(completedGroups, totalResults, DateTime.UtcNow);

        // Cacheia o resultado
        var payload = JsonSerializer.Serialize(result, JsonOptions);
        await _redisService.SetAsync(cacheKey, payload, CacheTtlSeconds);

        return result;
    }

    // ─── Queries por entidade ──────────────────────────────────────────

    private async Task<SearchResultGroup?> SearchAgentsAsync(
        string query, UserScopeAccess access, int maxResults, CancellationToken ct)
    {
        var agents = _db.Agents
            .AsNoTracking()
            .Where(a => a.DeletedAt == null)
            .Where(a => EF.Functions.ILike(a.Hostname, $"%{query}%")
                     || EF.Functions.ILike(a.DisplayName ?? "", $"%{query}%")
                     || EF.Functions.ILike(a.OperatingSystem ?? "", $"%{query}%")
                     || EF.Functions.ILike(a.LastIpAddress ?? "", $"%{query}%"));

        // Aplica filtro de escopo via join com sites
        if (!access.HasGlobalAccess)
        {
            var allowedClientIds = access.AllowedClientIds.ToHashSet();
            var allowedSiteIds = access.AllowedSiteIds.ToHashSet();
            if (allowedClientIds.Count == 0 && allowedSiteIds.Count == 0)
                return null;

            agents = from agent in agents
                     join site in _db.Sites.AsNoTracking() on agent.SiteId equals site.Id
                     where allowedClientIds.Contains(site.ClientId) || allowedSiteIds.Contains(agent.SiteId)
                     select agent;
        }

        var results = await agents
            .OrderBy(a => a.Hostname)
            .Take(maxResults)
            .Select(a => new { a.Id, a.Hostname, a.DisplayName, a.SiteId, a.OperatingSystem })
            .ToListAsync(ct);

        if (results.Count == 0) return null;

        // Enriquece com nomes de client/site
        var siteIds = results.Select(r => r.SiteId).Distinct().ToList();
        var siteMapping = await _db.Sites
            .AsNoTracking()
            .Where(s => siteIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.ClientId })
            .ToListAsync(ct);

        var clientIds = siteMapping.Select(s => s.ClientId).Distinct().ToList();
        var clientMapping = await _db.Clients
            .AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);

        var siteMap = siteMapping.ToDictionary(s => s.Id);
        var clientMap = clientMapping.ToDictionary(c => c.Id);

        var items = results.Select(r =>
        {
            var site = siteMap.GetValueOrDefault(r.SiteId);
            var client = site is not null ? clientMap.GetValueOrDefault(site.ClientId) : null;
            return new SearchResultItem(
                Id: r.Id,
                Title: r.DisplayName ?? r.Hostname,
                Subtitle: r.OperatingSystem,
                Description: r.Hostname,
                EntityType: "agent",
                ClientId: client?.Id,
                ClientName: client?.Name,
                SiteId: r.SiteId,
                SiteName: site?.Name,
                Url: $"/clients/{client?.Id}/sites/{r.SiteId}/agents/{r.Id}"
            );
        }).ToList();

        return new SearchResultGroup("agents", "Agentes", "monitor", items);
    }

    private async Task<SearchResultGroup?> SearchClientsAsync(
        string query, UserScopeAccess access, int maxResults, CancellationToken ct)
    {
        var clients = _db.Clients
            .AsNoTracking()
            .Where(c => EF.Functions.ILike(c.Name, $"%{query}%"))
            .Where(c => c.IsActive);

        if (!access.HasGlobalAccess && access.AllowedClientIds.Count > 0)
        {
            var allowed = access.AllowedClientIds.ToHashSet();
            clients = clients.Where(c => allowed.Contains(c.Id));
        }
        else if (!access.HasGlobalAccess)
        {
            return null;
        }

        var results = await clients
            .OrderBy(c => c.Name)
            .Take(maxResults)
            .Select(c => new { c.Id, c.Name, c.Notes })
            .ToListAsync(ct);

        if (results.Count == 0) return null;

        var items = results.Select(r =>
            new SearchResultItem(
                Id: r.Id,
                Title: r.Name,
                Subtitle: null,
                Description: r.Notes,
                EntityType: "client",
                ClientId: r.Id,
                ClientName: r.Name,
                SiteId: null,
                SiteName: null,
                Url: $"/clients/{r.Id}"
            )
        ).ToList();

        return new SearchResultGroup("clients", "Clientes", "building", items);
    }

    private async Task<SearchResultGroup?> SearchSitesAsync(
        string query, UserScopeAccess clientAccess, UserScopeAccess siteAccess, int maxResults, CancellationToken ct)
    {
        var sites = _db.Sites
            .AsNoTracking()
            .Where(s => EF.Functions.ILike(s.Name, $"%{query}%"))
            .Where(s => s.IsActive);

        // Filtro por escopo: acesso a Client ou Site
        if (!clientAccess.HasGlobalAccess)
        {
            var allowedClientIds = clientAccess.AllowedClientIds.ToHashSet();
            var allowedSiteIds = siteAccess.AllowedSiteIds.ToHashSet();

            if (allowedClientIds.Count == 0 && allowedSiteIds.Count == 0)
                return null;

            sites = sites.Where(s =>
                allowedClientIds.Contains(s.ClientId) ||
                allowedSiteIds.Contains(s.Id));
        }

        var results = await sites
            .OrderBy(s => s.Name)
            .Take(maxResults)
            .Select(s => new { s.Id, s.Name, s.ClientId, s.Notes })
            .ToListAsync(ct);

        if (results.Count == 0) return null;

        var clientIds = results.Select(r => r.ClientId).Distinct().ToList();
        var clientMapping = await _db.Clients
            .AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);

        var clientMap = clientMapping.ToDictionary(c => c.Id);

        var items = results.Select(r =>
        {
            var client = clientMap.GetValueOrDefault(r.ClientId);
            return new SearchResultItem(
                Id: r.Id,
                Title: r.Name,
                Subtitle: client?.Name,
                Description: r.Notes,
                EntityType: "site",
                ClientId: r.ClientId,
                ClientName: client?.Name,
                SiteId: r.Id,
                SiteName: r.Name,
                Url: $"/clients/{r.ClientId}/sites/{r.Id}"
            );
        }).ToList();

        return new SearchResultGroup("sites", "Sites", "layers", items);
    }

    private async Task<SearchResultGroup?> SearchTicketsAsync(
        string query, UserScopeAccess access, int maxResults, CancellationToken ct)
    {
        var tickets = _db.Tickets
            .AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .Where(t => EF.Functions.ILike(t.Title, $"%{query}%")
                     || EF.Functions.ILike(t.Description ?? "", $"%{query}%")
                     || EF.Functions.ILike(t.Category ?? "", $"%{query}%"));

        if (!access.HasGlobalAccess)
        {
            var allowedClientIds = access.AllowedClientIds.ToHashSet();
            var allowedSiteIds = access.AllowedSiteIds.ToHashSet();

            if (allowedClientIds.Count == 0 && allowedSiteIds.Count == 0)
                return null;

            tickets = tickets.Where(t =>
                allowedClientIds.Contains(t.ClientId) ||
                (t.SiteId.HasValue && allowedSiteIds.Contains(t.SiteId.Value)));
        }

        var results = await tickets
            .OrderByDescending(t => t.CreatedAt)
            .Take(maxResults)
            .Select(t => new { t.Id, t.Title, t.ClientId, t.SiteId, t.Category })
            .ToListAsync(ct);

        if (results.Count == 0) return null;

        // Enriquece com nomes de client
        var clientIds = results.Where(r => r.ClientId != Guid.Empty).Select(r => r.ClientId).Distinct().ToList();
        var clientMapping = clientIds.Count > 0
            ? await _db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Name }).ToListAsync(ct)
            : [];
        var clientMap = clientMapping.ToDictionary(c => c.Id);

        var items = results.Select(r =>
        {
            var client = clientMap.GetValueOrDefault(r.ClientId);
            return new SearchResultItem(
                Id: r.Id,
                Title: r.Title,
                Subtitle: r.Category,
                Description: null,
                EntityType: "ticket",
                ClientId: r.ClientId,
                ClientName: client?.Name,
                SiteId: r.SiteId,
                SiteName: null,
                Url: $"/tickets/{r.Id}"
            );
        }).ToList();

        return new SearchResultGroup("tickets", "Chamados", "ticket", items);
    }

    private async Task<SearchResultGroup?> SearchSoftwareAsync(
        string query, UserScopeAccess access, int maxResults, CancellationToken ct)
    {
        // Busca global primeiro no catálogo, depois filtra por escopo via agent → site → client
        var catalogMatches = await _db.SoftwareCatalogs
            .AsNoTracking()
            .Where(s => EF.Functions.ILike(s.Name, $"%{query}%")
                     || EF.Functions.ILike(s.Publisher ?? "", $"%{query}%"))
            .Select(s => new { s.Id, s.Name, s.Publisher })
            .Take(maxResults * 3) // Busca mais para filtrar por escopo depois
            .ToListAsync(ct);

        if (catalogMatches.Count == 0) return null;

        var softwareIds = catalogMatches.Select(s => s.Id).ToList();

        // Encontra agents que têm este software instalado, respeitando escopo
        var agentSoftwareQuery = _db.AgentSoftwareInventories
            .AsNoTracking()
            .Where(i => i.IsPresent)
            .Where(i => softwareIds.Contains(i.SoftwareId));

        // Aplica escopo de agent via join com sites
        var agentQuery = _db.Agents.AsNoTracking().Where(a => a.DeletedAt == null);

        if (!access.HasGlobalAccess)
        {
            var allowedClientIds = access.AllowedClientIds.ToHashSet();
            var allowedSiteIds = access.AllowedSiteIds.ToHashSet();
            if (allowedClientIds.Count == 0 && allowedSiteIds.Count == 0)
                return null;

            agentQuery = from a in agentQuery
                         join site in _db.Sites.AsNoTracking() on a.SiteId equals site.Id
                         where allowedClientIds.Contains(site.ClientId) || allowedSiteIds.Contains(a.SiteId)
                         select a;
        }

        // Evita join com colecao em memoria (catalogMatches), que nao e traduzivel em SQL.
        var scopedSoftware = from inv in agentSoftwareQuery
                             join a in agentQuery on inv.AgentId equals a.Id
                             join sw in _db.SoftwareCatalogs.AsNoTracking() on inv.SoftwareId equals sw.Id
                             select new
                             {
                                 sw.Name,
                                 sw.Publisher,
                                 sw.Id,
                                 inv.AgentId,
                                 a.SiteId
                             };

        var results = await scopedSoftware
            .Take(maxResults)
            .ToListAsync(ct);

        if (results.Count == 0) return null;

        // Enriquece com nomes de client/site
        var siteIds = results.Select(r => r.SiteId).Distinct().ToList();
        var siteMapping = await _db.Sites
            .AsNoTracking()
            .Where(s => siteIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.ClientId })
            .ToListAsync(ct);

        var clientIds = siteMapping.Select(s => s.ClientId).Distinct().ToList();
        var clientMapping = await _db.Clients
            .AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);

        var siteMap = siteMapping.ToDictionary(s => s.Id);
        var clientMap = clientMapping.ToDictionary(c => c.Id);

        // Deduplica por software
        var seen = new HashSet<Guid>();
        var items = new List<SearchResultItem>();

        foreach (var r in results)
        {
            if (!seen.Add(r.Id)) continue;
            var site = siteMap.GetValueOrDefault(r.SiteId);
            var client = site is not null ? clientMap.GetValueOrDefault(site.ClientId) : null;

            items.Add(new SearchResultItem(
                Id: r.Id,
                Title: r.Name,
                Subtitle: r.Publisher,
                Description: null,
                EntityType: "software",
                ClientId: client?.Id,
                ClientName: client?.Name,
                SiteId: r.SiteId,
                SiteName: site?.Name,
                Url: $"/software/{r.Id}"
            ));

            if (items.Count >= maxResults) break;
        }

        return items.Count > 0
            ? new SearchResultGroup("software", "Softwares", "package", items)
            : null;
    }

    private Task<SearchResultGroup?> SearchNavigationAsync(
        string query,
        int maxResults,
        IReadOnlyDictionary<ResourceType, bool> permissionMap,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var items = NavigationTargets
            .Where(target => NavigationTargetAllowed(target, permissionMap))
            .Where(target => NavigationTargetMatches(target, query))
            .Take(maxResults)
            .Select(target => new SearchResultItem(
                Id: DeterministicGuid($"navigation:{target.Url}"),
                Title: target.Title,
                Subtitle: target.Section,
                Description: target.Description,
                EntityType: "navigation",
                ClientId: null,
                ClientName: null,
                SiteId: null,
                SiteName: null,
                Url: target.Url))
            .ToList();

        SearchResultGroup? group = items.Count > 0
            ? new SearchResultGroup("navigation", "Navegação", "layers", items)
            : null;

        return Task.FromResult(group);
    }

    private async Task<SearchResultGroup?> SearchReportTemplatesAsync(
        string query,
        UserScopeAccess access,
        int maxResults,
        CancellationToken ct)
    {
        if (!HasAnyAccess(access))
            return null;

        var templates = _db.ReportTemplates
            .AsNoTracking()
            .Where(template => template.IsActive)
            .Where(template =>
                EF.Functions.ILike(template.Name, $"%{query}%") ||
                EF.Functions.ILike(template.Description ?? "", $"%{query}%") ||
                EF.Functions.ILike(template.Instructions ?? "", $"%{query}%"));

        if (!access.HasGlobalAccess)
        {
            var allowedClientIds = access.AllowedClientIds.ToHashSet();
            if (allowedClientIds.Count == 0 && access.AllowedSiteIds.Count > 0)
            {
                var allowedSiteIds = access.AllowedSiteIds.ToHashSet();
                var siteClientIds = await _db.Sites
                    .AsNoTracking()
                    .Where(site => allowedSiteIds.Contains(site.Id))
                    .Select(site => site.ClientId)
                    .Distinct()
                    .ToListAsync(ct);

                allowedClientIds.UnionWith(siteClientIds);
            }

            if (allowedClientIds.Count == 0)
                return null;

            templates = templates.Where(template =>
                template.ClientId == null ||
                (template.ClientId.HasValue && allowedClientIds.Contains(template.ClientId.Value)));
        }

        var results = await templates
            .OrderBy(template => template.Name)
            .Take(maxResults)
            .Select(template => new
            {
                template.Id,
                template.Name,
                template.Description,
                template.DatasetType,
                template.DefaultFormat,
                template.ClientId
            })
            .ToListAsync(ct);

        if (results.Count == 0)
            return null;

        var clientIds = results
            .Where(item => item.ClientId.HasValue)
            .Select(item => item.ClientId!.Value)
            .Distinct()
            .ToList();

        var clientMap = clientIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Clients
                .AsNoTracking()
                .Where(client => clientIds.Contains(client.Id))
                .ToDictionaryAsync(client => client.Id, client => client.Name, ct);

        var items = results.Select(item =>
        {
            var clientName = item.ClientId.HasValue && clientMap.TryGetValue(item.ClientId.Value, out var name)
                ? name
                : null;

            return new SearchResultItem(
                Id: item.Id,
                Title: item.Name,
                Subtitle: $"{item.DatasetType} • {item.DefaultFormat}",
                Description: item.Description,
                EntityType: "report-template",
                ClientId: item.ClientId,
                ClientName: clientName,
                SiteId: null,
                SiteName: null,
                Url: $"/reports/run?templateId={item.Id}");
        }).ToList();

        return new SearchResultGroup("reports", "Relatórios", "package", items);
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private static bool HasAnyAccess(UserScopeAccess access)
        => access.HasGlobalAccess || access.AllowedClientIds.Count > 0 || access.AllowedSiteIds.Count > 0;

    private static bool NavigationTargetMatches(NavigationTarget target, string query)
    {
        if (ContainsInsensitive(target.Title, query) || ContainsInsensitive(target.Url, query))
            return true;

        if (!string.IsNullOrWhiteSpace(target.Section) && ContainsInsensitive(target.Section, query))
            return true;

        if (!string.IsNullOrWhiteSpace(target.Description) && ContainsInsensitive(target.Description, query))
            return true;

        return !string.IsNullOrWhiteSpace(target.Keywords) && ContainsInsensitive(target.Keywords, query);
    }

    private static bool NavigationTargetAllowed(
        NavigationTarget target,
        IReadOnlyDictionary<ResourceType, bool> permissionMap)
    {
        if (target.AnyOfResources.Length == 0)
            return true;

        foreach (var resource in target.AnyOfResources)
        {
            if (permissionMap.TryGetValue(resource, out var hasAccess) && hasAccess)
                return true;
        }

        return false;
    }

    private async Task<IReadOnlyDictionary<ResourceType, bool>> ResolveNavigationPermissionsAsync(
        UserScopeAccess agentAccess,
        UserScopeAccess clientAccess,
        UserScopeAccess siteAccess,
        UserScopeAccess ticketAccess,
        UserScopeAccess reportAccess)
    {
        var map = new Dictionary<ResourceType, bool>
        {
            [ResourceType.Agents] = HasAnyAccess(agentAccess),
            [ResourceType.Clients] = HasAnyAccess(clientAccess),
            [ResourceType.Sites] = HasAnyAccess(siteAccess),
            [ResourceType.Tickets] = HasAnyAccess(ticketAccess),
            [ResourceType.Reports] = HasAnyAccess(reportAccess)
        };

        var missingResources = NavigationTargets
            .SelectMany(target => target.AnyOfResources)
            .Distinct()
            .Where(resource => !map.ContainsKey(resource))
            .ToList();

        foreach (var resource in missingResources)
        {
            var access = await _scopeContext.GetAccessAsync(resource, ActionType.View);
            map[resource] = HasAnyAccess(access);
        }

        return map;
    }

    private static bool ContainsInsensitive(string source, string value)
        => source.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static Guid DeterministicGuid(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(hash[..16]);
    }

    private static NavigationTarget[] BuildNavigationTargets() =>
    [
        new("Dashboard", "Principal", "Visão geral do ambiente.", "/", "painel home inicio dashboard", [ResourceType.Dashboard]),
        new("Clientes", "Navegação", "Lista de clientes.", "/clients", "clientes customer customer list", [ResourceType.Clients]),
        new("Sites", "Navegação", "Lista de sites.", "/sites", "sites unidades locais", [ResourceType.Sites]),
        new("Agentes", "Navegação", "Inventário de agentes.", "/agents", "agentes dispositivos hosts endpoints", [ResourceType.Agents]),
        new("Logs", "Navegação", "Eventos e logs do sistema.", "/logs", "logs eventos auditoria", [ResourceType.Logs]),
        new("Deploy", "Navegação", "Distribuição e instalação.", "/deploy", "deploy instalacao instalador token", [ResourceType.Deployment]),
        new("Chamados", "Suporte", "Fila de chamados.", "/tickets", "tickets chamados suporte", [ResourceType.Tickets]),
        new("Conhecimento", "Suporte", "Base de conhecimento.", "/knowledge", "kb base conhecimento artigos", [ResourceType.KnowledgeBase]),
        new("Alertas", "Suporte", "Regras e eventos de alerta.", "/tickets/alerts", "alertas regras", [ResourceType.Tickets]),
        new("SLA, Calendários e Perfis", "Suporte", "Gestão de SLA e horários.", "/tickets/sla", "sla calendario perfis", [ResourceType.Tickets]),
        new("Departamentos", "Suporte", "Configuração de departamentos.", "/tickets/departments", "departamentos", [ResourceType.Tickets]),
        new("Workflow Profiles", "Suporte", "Perfis de workflow.", "/settings/workflow-profiles", "workflow profiles", [ResourceType.Tickets]),
        new("Inventário Detalhado", "Softwares", "Inventário de software instalado.", "/software/inventory", "software inventario", [ResourceType.Agents]),
        new("Loja de aplicativos", "Softwares", "Catálogo de aplicativos.", "/software/store", "software loja app store", [ResourceType.AppStore]),
        new("Automação", "Automação", "Visão geral de automações.", "/automation", "automacao automacao geral overview", [ResourceType.Automation]),
        new("Scripts", "Automação", "Biblioteca de scripts.", "/automation/scripts", "scripts automacao", [ResourceType.Automation]),
        new("Tarefas", "Automação", "Tarefas automatizadas.", "/automation/tasks", "tarefas jobs automacao", [ResourceType.Automation]),
        new("Operações", "Automação", "Execuções operacionais.", "/automation/operations", "operacoes runs execucoes", [ResourceType.Automation]),
        new("Auditoria", "Automação", "Auditoria das automações.", "/automation/audit", "auditoria automation", [ResourceType.Automation]),
        new("Labels Automáticas", "Automação", "Gerenciamento de labels automáticas.", "/settings/agent-labels", "labels tags automaticas", [ResourceType.Automation]),
        new("Templates de Relatórios", "Relatórios", "Biblioteca de templates de relatório.", "/reports/templates", "relatorios relatorios templates", [ResourceType.Reports]),
        new("Execuções de Relatórios", "Relatórios", "Histórico e status de execuções.", "/reports/executions", "relatorios execucoes processamento", [ResourceType.Reports]),
        new("Perfil e Segurança", "Identidade", "Configurações de autenticação.", "/identity/authentication", "perfil seguranca autenticacao", []),
        new("Usuários e Acesso", "Identidade", "Gerenciamento de usuários.", "/identity/users", "usuarios acesso", [ResourceType.Users]),
        new("Grupos de Usuários", "Identidade", "Gerenciamento de grupos.", "/identity/groups", "grupos usuarios", [ResourceType.Users]),
        new("Roles e Permissões", "Identidade", "Controle de permissões.", "/identity/roles", "roles permissoes", [ResourceType.Users]),
        new("Perfis Mesh", "Identidade", "Perfis do MeshCentral.", "/identity/mesh-profiles", "mesh profiles", [ResourceType.Users]),
        new("Config MeshCentral", "Identidade", "Configurações do MeshCentral.", "/identity/mesh-central", "mesh central config", [ResourceType.Users, ResourceType.SiteConfig]),
        new("Diagnostics MeshCentral", "Identidade", "Diagnóstico do MeshCentral.", "/identity/mesh-diagnostics", "mesh diagnostics", [ResourceType.SiteConfig]),
        new("Node Links Mesh", "Identidade", "Vínculos de nós Mesh.", "/identity/mesh-node-links", "mesh node links", [ResourceType.Agents, ResourceType.SiteConfig]),
        new("Configurações Gerais", "Configurações", "Configurações globais.", "/settings", "configuracoes settings geral", [ResourceType.ServerConfig]),
        new("Workflow", "Configurações", "Configuração de workflows.", "/settings/workflow", "workflow configuracoes", [ResourceType.Tickets]),
        new("Auditoria Config", "Configurações", "Auditoria de configurações.", "/settings/audit", "auditoria configuracao", [ResourceType.Logs]),
        new("Campos Personalizados", "Configurações", "Campos customizáveis.", "/settings/custom-fields", "campos personalizados custom fields", [ResourceType.ServerConfig]),
        new("Branding", "Configurações", "Identidade visual da plataforma.", "/settings/branding", "branding tema marca", [ResourceType.ServerConfig])
    ];

    private static UniversalSearchResult EmptyResult()
        => new([], 0, DateTime.UtcNow);

    private sealed record NavigationTarget(
        string Title,
        string Section,
        string? Description,
        string Url,
        string? Keywords,
        ResourceType[] AnyOfResources);
}
