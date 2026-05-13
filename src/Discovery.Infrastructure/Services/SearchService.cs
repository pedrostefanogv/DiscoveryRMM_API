using System.Text.Json;
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

        // Executa consultas em sequencia com timeout parcial
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SearchTimeoutMs);

        var searchSteps = new Func<CancellationToken, Task<SearchResultGroup?>>[]
        {
            token => SearchAgentsAsync(query, agentAccess, maxResults, token),
            token => SearchClientsAsync(query, clientAccess, maxResults, token),
            token => SearchSitesAsync(query, clientAccess, siteAccess, maxResults, token),
            token => SearchTicketsAsync(query, ticketAccess, maxResults, token),
            token => SearchSoftwareAsync(query, agentAccess, maxResults, token),
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

    // ─── Helpers ───────────────────────────────────────────────────────

    private static UniversalSearchResult EmptyResult()
        => new([], 0, DateTime.UtcNow);
}
