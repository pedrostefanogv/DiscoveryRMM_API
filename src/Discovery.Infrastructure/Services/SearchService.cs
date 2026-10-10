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

    // Cache curtíssimo do mapa "agentId -> usuário logado ao vivo". Evita varrer
    // o keyspace de heartbeat do Redis a cada tecla digitada na busca global.
    private const int LiveLoggedUserCacheTtlSeconds = 15;
    private const string LiveLoggedUserCacheKey = "search:live-logged-users:v1";
    // Orçamento próprio do cruzamento ao vivo. O timeout da busca (5s) é
    // compartilhado por 7 grupos sequenciais; este refinamento não pode consumi-lo.
    private const int LiveLoggedUserBudgetMs = 1500;

    private readonly DiscoveryDbContext _db;
    private readonly IScopeContext _scopeContext;
    private readonly IRedisService _redisService;
    private readonly IHeartbeatCacheService _heartbeatCache;

    public SearchService(
        DiscoveryDbContext db,
        IScopeContext scopeContext,
        IRedisService redisService,
        IHeartbeatCacheService heartbeatCache)
    {
        _db = db;
        _scopeContext = scopeContext;
        _redisService = redisService;
        _heartbeatCache = heartbeatCache;
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
        var likePattern = LikePattern(query);
        var agents = _db.Agents
            .AsNoTracking()
            .Where(a => a.DeletedAt == null)
            .Where(a => EF.Functions.ILike(a.Hostname, likePattern)
                     || EF.Functions.ILike(a.DisplayName ?? "", likePattern)
                     || EF.Functions.ILike(a.OperatingSystem ?? "", likePattern)
                     || EF.Functions.ILike(a.LastIpAddress ?? "", likePattern)
                     || EF.Functions.ILike(a.LoggedUser ?? "", likePattern));

        // Aplica filtro de escopo via join com sites
        if (!access.HasGlobalAccess)
        {
            if (access.AllowedClientIds.Count == 0 && access.AllowedSiteIds.Count == 0)
                return null;

            agents = ApplyAgentScope(agents, access);
        }

        var results = await agents
            .OrderBy(a => a.Hostname)
            .Take(maxResults)
            .Select(a => new { a.Id, a.Hostname, a.DisplayName, a.SiteId, a.OperatingSystem, a.LoggedUser })
            .ToListAsync(ct);

        // ── Cruzamento com o usuário logado AO VIVO (heartbeat no Redis) ────
        // agents.logged_user guarda apenas o "último conhecido" e só é reescrito
        // no sync de inventário (até 6h). O heartbeat sabe quem está logado
        // AGORA — é o valor que o card do agent exibe. Sem este cruzamento a
        // busca só acharia o agent no próximo inventário, ou nunca para um
        // agent recém-logado com o inventário antigo já sincronizado.
        var liveUsers = new Dictionary<Guid, string>();
        // O cruzamento só roda quando o banco NÃO preencheu a página: o ganho é
        // exatamente nos casos em que o "último conhecido" está vazio/desatualizado
        // (a queixa original). Em frota grande a varredura de heartbeat tem custo,
        // ainda que limitada pelo orçamento próprio e pelo cache curto.
        if (results.Count < maxResults)
        {
            var liveMap = await GetLiveLoggedUserMapAsync(ct);
            if (liveMap.Count > 0)
            {
                var foundIds = results.Select(r => r.Id).ToHashSet();
                var liveIds = liveMap
                    .Where(pair => !foundIds.Contains(pair.Key)
                                   && pair.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Key)
                    .ToList();

                if (liveIds.Count > 0)
                {
                    var liveAgents = _db.Agents
                        .AsNoTracking()
                        .Where(a => a.DeletedAt == null && liveIds.Contains(a.Id));
                    if (!access.HasGlobalAccess)
                        liveAgents = ApplyAgentScope(liveAgents, access);

                    var liveRows = await liveAgents
                        .OrderBy(a => a.Hostname)
                        .Take(maxResults)
                        .Select(a => new { a.Id, a.Hostname, a.DisplayName, a.SiteId, a.OperatingSystem, a.LoggedUser })
                        .ToListAsync(ct);

                    if (liveRows.Count > 0)
                    {
                        results.AddRange(liveRows);
                        results = results.OrderBy(r => r.Hostname).Take(maxResults).ToList();
                    }
                }

                foreach (var row in results)
                {
                    if (liveMap.TryGetValue(row.Id, out var liveUser) && !string.IsNullOrWhiteSpace(liveUser))
                        liveUsers[row.Id] = liveUser;
                }
            }
        }

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
            // Prefere o usuário AO VIVO (heartbeat) ao "último conhecido" do DB,
            // para o resultado da busca bater com o que o card do agent exibe.
            var loggedUser = liveUsers.TryGetValue(r.Id, out var liveUser) && !string.IsNullOrWhiteSpace(liveUser)
                ? liveUser
                : r.LoggedUser;
            return new SearchResultItem(
                Id: r.Id,
                Title: r.DisplayName ?? r.Hostname,
                Subtitle: r.OperatingSystem,
                // Mostra o usuário logado quando a busca casou por ele.
                Description: string.IsNullOrWhiteSpace(loggedUser)
                    ? r.Hostname
                    : $"{r.Hostname} · {loggedUser}",
                EntityType: "agent",
                ClientId: client?.Id,
                ClientName: client?.Name,
                SiteId: r.SiteId,
                SiteName: site?.Name,
                Url: $"/agents/{r.Id}"
            );
        }).ToList();

        return new SearchResultGroup("agents", "Agentes", "monitor", items);
    }

    /// <summary>
    /// Restringe a consulta de agentes ao escopo do usuário (Global → Client →
    /// Site) via join com <c>sites</c>. Use somente quando
    /// <see cref="UserScopeAccess.HasGlobalAccess"/> for falso.
    /// </summary>
    private IQueryable<Agent> ApplyAgentScope(IQueryable<Agent> agents, UserScopeAccess access)
    {
        var allowedClientIds = access.AllowedClientIds.ToHashSet();
        var allowedSiteIds = access.AllowedSiteIds.ToHashSet();
        return from agent in agents
               join site in _db.Sites.AsNoTracking() on agent.SiteId equals site.Id
               where allowedClientIds.Contains(site.ClientId) || allowedSiteIds.Contains(agent.SiteId)
               select agent;
    }

    /// <summary>
    /// Mapa <c>agentId → usuário logado AO VIVO</c> a partir do cache de
    /// heartbeat (Redis). Usa um cache curto em Redis para não varrer o keyspace
    /// de heartbeat a cada tecla digitada. Best-effort: qualquer falha de Redis
    /// degrada para "sem usuário ao vivo", nunca quebra a busca.
    /// </summary>
    private async Task<Dictionary<Guid, string>> GetLiveLoggedUserMapAsync(CancellationToken ct)
    {
        try
        {
            var cached = await _redisService.GetAsync(LiveLoggedUserCacheKey);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<Guid, string>>(cached, JsonOptions);
                if (parsed is not null)
                    return parsed;
            }
        }
        catch
        {
            // cache indisponível — segue para a leitura direta do heartbeat
        }

        var map = new Dictionary<Guid, string>();
        var scanCompleted = false;
        try
        {
            using var liveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            liveCts.CancelAfter(LiveLoggedUserBudgetMs);

            var entries = await _heartbeatCache.GetActiveHeartbeatsAsync(liveCts.Token);
            foreach (var entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.LoggedUser))
                    map[entry.AgentId] = entry.LoggedUser;
            }

            scanCompleted = !liveCts.IsCancellationRequested;
        }
        catch
        {
            return map;
        }

        // Varredura parcial não é cacheada: envenenaria o cache curto com um mapa
        // incompleto e a busca perderia agentes recém-logados por até o TTL.
        if (!scanCompleted)
            return map;

        try
        {
            await _redisService.SetAsync(
                LiveLoggedUserCacheKey,
                JsonSerializer.Serialize(map, JsonOptions),
                LiveLoggedUserCacheTtlSeconds);
        }
        catch
        {
            // cache best-effort
        }

        return map;
    }

    private async Task<SearchResultGroup?> SearchClientsAsync(
        string query, UserScopeAccess access, int maxResults, CancellationToken ct)
    {
        var likePattern = LikePattern(query);
        var clients = _db.Clients
            .AsNoTracking()
            .Where(c => EF.Functions.ILike(c.Name, likePattern))
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
        var likePattern = LikePattern(query);
        var sites = _db.Sites
            .AsNoTracking()
            .Where(s => EF.Functions.ILike(s.Name, likePattern))
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
            .WhereMatchesText(_db.TicketAnswers, query);

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
        var likePattern = LikePattern(query);
        var catalogMatches = await _db.SoftwareCatalogs
            .AsNoTracking()
            .Where(s => EF.Functions.ILike(s.Name, likePattern)
                     || EF.Functions.ILike(s.Publisher ?? "", likePattern))
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

        var likePattern = LikePattern(query);
        var templates = _db.ReportTemplates
            .AsNoTracking()
            .Where(template => template.IsActive)
            .Where(template =>
                EF.Functions.ILike(template.Name, likePattern) ||
                EF.Functions.ILike(template.Description ?? "", likePattern) ||
                EF.Functions.ILike(template.Instructions ?? "", likePattern));

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
        new("Configurações Gerais", "Configurações", "Configurações globais.", "/settings", "configuracoes settings geral", [ResourceType.ServerConfig]),
        new("Configuração do Servidor", "Configurações", "Valores base herdados por clientes e sites.", "/settings/server", "servidor server config configuracao hierarquia", [ResourceType.ServerConfig]),
        new("Configuração de Cliente", "Configurações", "Sobrescritas por cliente.", "/settings/client", "cliente client config configuracao", [ResourceType.ClientConfig]),
        new("Configuração de Site", "Configurações", "Sobrescritas por site.", "/settings/site", "site config configuracao", [ResourceType.SiteConfig]),
        new("Notificações", "Configurações", "Canais de notificação.", "/settings/notifications", "notificacoes notifications canais", [ResourceType.ServerConfig]),
        new("Agent Updates", "Configurações", "Atualizações do agente.", "/settings/agent-updates", "agent updates atualizacoes", [ResourceType.ServerConfig]),
        new("Workflow", "Configurações", "Configuração de workflows.", "/settings/workflow", "workflow configuracoes", [ResourceType.Tickets]),
        new("Auditoria Config", "Configurações", "Auditoria de configurações.", "/settings/audit", "auditoria configuracao", [ResourceType.Logs]),
        new("Campos Personalizados", "Configurações", "Campos customizáveis.", "/settings/custom-fields", "campos personalizados custom fields", [ResourceType.ServerConfig]),
        new("Branding", "Configurações", "Identidade visual da plataforma.", "/settings/branding", "branding tema marca", [ResourceType.ServerConfig])
    ];

    /// <summary>
    /// Escapa os curingas do LIKE/ILIKE (% _ \) para que o texto digitado seja
    /// tratado como literal. O PostgreSQL usa '\' como escape padrão.
    /// </summary>
    private static string LikePattern(string value)
        => $"%{value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";

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
