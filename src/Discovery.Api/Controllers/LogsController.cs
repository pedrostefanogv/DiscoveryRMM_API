using System.Text.Json;
using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using CoreLogLevel = Discovery.Core.Enums.LogLevel;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class LogsController : ControllerBase
{
    private readonly ILogRepository _logRepo;
    private readonly IClientRepository _clientRepo;
    private readonly IAgentRepository _agentRepo;
    private readonly ISiteRepository _siteRepo;
    private readonly IScopeContext _scopeContext;

    public LogsController(
        ILogRepository logRepo,
        IClientRepository clientRepo,
        IAgentRepository agentRepo,
        ISiteRepository siteRepo,
        IScopeContext scopeContext)
    {
        _logRepo = logRepo;
        _clientRepo = clientRepo;
        _agentRepo = agentRepo;
        _siteRepo = siteRepo;
        _scopeContext = scopeContext;
    }

    [HttpGet]
    [RequirePermission(ResourceType.Logs, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> Query(
        [FromQuery] Guid? clientId,
        [FromQuery] Guid? siteId,
        [FromQuery] Guid? agentId,
        [FromQuery] LogType? type,
        [FromQuery] CoreLogLevel? level,
        [FromQuery] LogSource? source,
        [FromQuery] string? search,
        [FromQuery] string? traceId,
        [FromQuery] string? correlationId,
        [FromQuery] string? requestPath,
        [FromQuery] int? statusCode,
        [FromQuery] string? period,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0)
    {
        if (!TryResolvePeriod(period, from, to, out from, out to, out var periodError))
            return BadRequest(periodError);

        var scope = await _scopeContext.GetAccessAsync(ResourceType.Logs, ActionType.View);

        if (agentId.HasValue)
        {
            var agent = await _agentRepo.GetByIdAsync(agentId.Value);
            if (agent is null)
                return NotFound("Agent not found.");

            siteId = agent.SiteId;
            var site = await _siteRepo.GetByIdAsync(agent.SiteId);
            if (site is null)
                return NotFound("Site not found for agent.");

            clientId = site.ClientId;

            if (!HasScopeAccess(scope, site.ClientId, site.Id))
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "Permissão insuficiente para este escopo." });
        }
        else if (siteId.HasValue)
        {
            var site = await _siteRepo.GetByIdAsync(siteId.Value);
            if (site is null)
                return NotFound("Site not found.");

            if (clientId.HasValue && clientId.Value != site.ClientId)
                return BadRequest("clientId does not match the provided siteId.");

            clientId = site.ClientId;

            if (!HasScopeAccess(scope, site.ClientId, site.Id))
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "Permissão insuficiente para este escopo." });
        }
        else if (clientId.HasValue && !HasScopeAccess(scope, clientId.Value, null))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Permissão insuficiente para este escopo." });
        }

        var query = new LogQuery
        {
            ClientId = clientId,
            SiteId = siteId,
            AgentId = agentId,
            Type = type,
            Level = level,
            Source = source,
            SearchText = search,
            TraceId = traceId,
            CorrelationId = correlationId,
            RequestPath = requestPath,
            StatusCode = statusCode,
            PeriodPreset = period,
            From = from,
            To = to,
            HasGlobalAccess = scope.HasGlobalAccess,
            AllowedClientIds = scope.AllowedClientIds,
            AllowedSiteIds = scope.AllowedSiteIds,
            Limit = limit,
            Offset = offset
        };

        var logs = await _logRepo.QueryAsync(query);
        return Ok(logs);
    }

    [HttpGet("page")]
    [RequirePermission(ResourceType.Logs, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> QueryPage(
        [FromQuery] Guid? clientId,
        [FromQuery] Guid? siteId,
        [FromQuery] Guid? agentId,
        [FromQuery] LogType? type,
        [FromQuery] CoreLogLevel? level,
        [FromQuery] LogSource? source,
        [FromQuery] string? search,
        [FromQuery] string? traceId,
        [FromQuery] string? correlationId,
        [FromQuery] string? requestPath,
        [FromQuery] int? statusCode,
        [FromQuery] string? period,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 100)
    {
        if (!TryResolvePeriod(period, from, to, out from, out to, out var periodError))
            return BadRequest(periodError);

        if (!TryDecodeCursor(cursor, out var cursorCreatedAtUtc, out var cursorId))
            return BadRequest("cursor is invalid.");

        var scope = await _scopeContext.GetAccessAsync(ResourceType.Logs, ActionType.View);

        var scopeValidation = await ValidateAndResolveScopeAsync(scope, clientId, siteId, agentId);
        if (scopeValidation.ErrorResult is not null)
            return scopeValidation.ErrorResult;

        var query = new LogQuery
        {
            ClientId = scopeValidation.ClientId,
            SiteId = scopeValidation.SiteId,
            AgentId = scopeValidation.AgentId,
            Type = type,
            Level = level,
            Source = source,
            SearchText = search,
            TraceId = traceId,
            CorrelationId = correlationId,
            RequestPath = requestPath,
            StatusCode = statusCode,
            PeriodPreset = period,
            From = from,
            To = to,
            CursorCreatedAtUtc = cursorCreatedAtUtc,
            CursorId = cursorId,
            HasGlobalAccess = scope.HasGlobalAccess,
            AllowedClientIds = scope.AllowedClientIds,
            AllowedSiteIds = scope.AllowedSiteIds,
            Limit = limit
        };

        var page = await _logRepo.QueryPageAsync(query);
        var hasMore = page.Count > Math.Clamp(limit, 1, 500);
        var items = hasMore ? page.Take(Math.Clamp(limit, 1, 500)).ToList() : page.ToList();
        var nextCursor = hasMore ? EncodeCursor(items[^1].CreatedAt, items[^1].Id) : null;

        return Ok(new LogCursorPageDto(
            items,
            items.Count,
            cursor,
            nextCursor,
            hasMore,
            Math.Clamp(limit, 1, 500),
            search,
            traceId,
            correlationId,
            requestPath,
            statusCode,
            period,
            from,
            to));
    }

    [HttpGet("summary")]
    [RequirePermission(ResourceType.Logs, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> QuerySummary(
        [FromQuery] Guid? clientId,
        [FromQuery] Guid? siteId,
        [FromQuery] Guid? agentId,
        [FromQuery] LogType? type,
        [FromQuery] CoreLogLevel? level,
        [FromQuery] LogSource? source,
        [FromQuery] string? search,
        [FromQuery] string? traceId,
        [FromQuery] string? correlationId,
        [FromQuery] string? requestPath,
        [FromQuery] int? statusCode,
        [FromQuery] string? period,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to)
    {
        if (!TryResolvePeriod(period, from, to, out from, out to, out var periodError))
            return BadRequest(periodError);

        var scope = await _scopeContext.GetAccessAsync(ResourceType.Logs, ActionType.View);
        var scopeValidation = await ValidateAndResolveScopeAsync(scope, clientId, siteId, agentId);
        if (scopeValidation.ErrorResult is not null)
            return scopeValidation.ErrorResult;

        var query = new LogQuery
        {
            ClientId = scopeValidation.ClientId,
            SiteId = scopeValidation.SiteId,
            AgentId = scopeValidation.AgentId,
            Type = type,
            Level = level,
            Source = source,
            SearchText = search,
            TraceId = traceId,
            CorrelationId = correlationId,
            RequestPath = requestPath,
            StatusCode = statusCode,
            PeriodPreset = period,
            From = from,
            To = to,
            HasGlobalAccess = scope.HasGlobalAccess,
            AllowedClientIds = scope.AllowedClientIds,
            AllowedSiteIds = scope.AllowedSiteIds
        };

        var summary = await _logRepo.GetSummaryAsync(query);
        var clients = (await _clientRepo.GetAllAsync(includeInactive: true)).ToDictionary(item => item.Id, item => item.Name);

        var allSites = new List<Site>();
        foreach (var currentClientId in clients.Keys)
            allSites.AddRange(await _siteRepo.GetByClientIdAsync(currentClientId, includeInactive: true));
        var sites = allSites.GroupBy(item => item.Id).Select(group => group.First()).ToDictionary(item => item.Id, item => item.Name);

        var agents = (await _agentRepo.GetAllAsync()).ToDictionary(item => item.Id, item => item.DisplayName ?? item.Hostname);

        return Ok(new LogSummaryDto(
            summary.Total,
            search,
            traceId,
            correlationId,
            requestPath,
            statusCode,
            period,
            from,
            to,
            summary.Levels,
            summary.Sources,
            summary.Types,
            summary.Clients.Select(item => new LogScopeFacetCountDto(item.Id, clients.GetValueOrDefault(item.Id), item.Count)).ToList(),
            summary.Sites.Select(item => new LogScopeFacetCountDto(item.Id, sites.GetValueOrDefault(item.Id), item.Count)).ToList(),
            summary.Agents.Select(item => new LogScopeFacetCountDto(item.Id, agents.GetValueOrDefault(item.Id), item.Count)).ToList()));
    }

    [HttpGet("scope-options")]
    [RequirePermission(ResourceType.Logs, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetScopeOptions()
    {
        var scope = await _scopeContext.GetAccessAsync(ResourceType.Logs, ActionType.View);
        var allClients = (await _clientRepo.GetAllAsync(includeInactive: true)).ToList();

        var allSites = new List<Site>();
        foreach (var client in allClients)
            allSites.AddRange(await _siteRepo.GetByClientIdAsync(client.Id, includeInactive: true));

        if (!scope.HasGlobalAccess)
        {
            var allowedClientIds = scope.AllowedClientIds.ToHashSet();
            var allowedSiteIds = scope.AllowedSiteIds.ToHashSet();

            allSites = allSites
                .Where(site => allowedClientIds.Contains(site.ClientId) || allowedSiteIds.Contains(site.Id))
                .ToList();

            var visibleClientIds = allSites.Select(site => site.ClientId)
                .Concat(scope.AllowedClientIds)
                .Distinct()
                .ToHashSet();

            allClients = allClients.Where(client => visibleClientIds.Contains(client.Id)).ToList();
        }

        var visibleSiteIds = allSites.Select(site => site.Id).ToHashSet();
        var agents = (await _agentRepo.GetAllAsync())
            .Where(agent => scope.HasGlobalAccess || visibleSiteIds.Contains(agent.SiteId))
            .Select(agent => new
            {
                agent.Id,
                Label = agent.DisplayName ?? agent.Hostname,
                agent.Hostname,
                agent.SiteId,
                agent.Status
            })
            .OrderBy(agent => agent.Label)
            .ToList();

        var clients = allClients
            .Select(client => new { client.Id, client.Name, client.IsActive })
            .OrderBy(client => client.Name)
            .ToList();

        var sites = allSites
            .Select(site => new { site.Id, site.ClientId, site.Name, site.IsActive })
            .OrderBy(site => site.Name)
            .ToList();

        return Ok(new
        {
            canViewAll = scope.HasGlobalAccess,
            clients,
            sites,
            agents,
            logLevels = Enum.GetValues<CoreLogLevel>().Select(value => new { value = value.ToString(), id = (int)value }),
            logSources = Enum.GetValues<LogSource>().Select(value => new { value = value.ToString(), id = (int)value }),
            logTypes = Enum.GetValues<LogType>().Select(value => new { value = value.ToString(), id = (int)value })
        });
    }

    [HttpPost]
    [RequirePermission(ResourceType.Logs, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateLogRequest request)
    {
        if (request.AgentId is null && request.SiteId is null && request.ClientId is null)
            return BadRequest("clientId, siteId or agentId is required.");

        Guid? clientId = request.ClientId;
        Guid? siteId = request.SiteId;
        Guid? agentId = request.AgentId;

        if (agentId.HasValue)
        {
            var agent = await _agentRepo.GetByIdAsync(agentId.Value);
            if (agent is null) return NotFound("Agent not found.");

            siteId = agent.SiteId;
            var site = await _siteRepo.GetByIdAsync(agent.SiteId);
            if (site is null) return NotFound("Site not found for agent.");
            clientId = site.ClientId;
        }
        else if (siteId.HasValue && !clientId.HasValue)
        {
            var site = await _siteRepo.GetByIdAsync(siteId.Value);
            if (site is null) return NotFound("Site not found.");
            clientId = site.ClientId;
        }

        string? dataJson = null;
        if (request.DataJson.HasValue && request.DataJson.Value.ValueKind != JsonValueKind.Null)
            dataJson = request.DataJson.Value.GetRawText();

        var entry = new LogEntry
        {
            ClientId = clientId,
            SiteId = siteId,
            AgentId = agentId,
            Type = request.Type,
            Level = request.Level,
            Source = request.Source,
            Message = request.Message,
            DataJson = dataJson
        };

        var created = await _logRepo.CreateAsync(entry);
        return Ok(created);
    }

    private static bool HasScopeAccess(UserScopeAccess scope, Guid clientId, Guid? siteId)
    {
        if (scope.HasGlobalAccess)
            return true;

        if (scope.AllowedClientIds.Contains(clientId))
            return true;

        return siteId.HasValue && scope.AllowedSiteIds.Contains(siteId.Value);
    }

    private async Task<(IActionResult? ErrorResult, Guid? ClientId, Guid? SiteId, Guid? AgentId)> ValidateAndResolveScopeAsync(
        UserScopeAccess scope,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId)
    {
        if (agentId.HasValue)
        {
            var agent = await _agentRepo.GetByIdAsync(agentId.Value);
            if (agent is null)
                return (NotFound("Agent not found."), null, null, null);

            siteId = agent.SiteId;
            var site = await _siteRepo.GetByIdAsync(agent.SiteId);
            if (site is null)
                return (NotFound("Site not found for agent."), null, null, null);

            clientId = site.ClientId;

            if (!HasScopeAccess(scope, site.ClientId, site.Id))
                return (StatusCode(StatusCodes.Status403Forbidden, new { message = "Permissão insuficiente para este escopo." }), null, null, null);
        }
        else if (siteId.HasValue)
        {
            var site = await _siteRepo.GetByIdAsync(siteId.Value);
            if (site is null)
                return (NotFound("Site not found."), null, null, null);

            if (clientId.HasValue && clientId.Value != site.ClientId)
                return (BadRequest("clientId does not match the provided siteId."), null, null, null);

            clientId = site.ClientId;

            if (!HasScopeAccess(scope, site.ClientId, site.Id))
                return (StatusCode(StatusCodes.Status403Forbidden, new { message = "Permissão insuficiente para este escopo." }), null, null, null);
        }
        else if (clientId.HasValue && !HasScopeAccess(scope, clientId.Value, null))
        {
            return (StatusCode(StatusCodes.Status403Forbidden, new { message = "Permissão insuficiente para este escopo." }), null, null, null);
        }

        return (null, clientId, siteId, agentId);
    }

    private static bool TryResolvePeriod(
        string? period,
        DateTime? requestedFrom,
        DateTime? requestedTo,
        out DateTime? from,
        out DateTime? to,
        out string? error)
    {
        from = requestedFrom;
        to = requestedTo;
        error = null;

        if (string.IsNullOrWhiteSpace(period))
            return true;

        var normalized = period.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;

        TimeSpan window = normalized switch
        {
            "15m" => TimeSpan.FromMinutes(15),
            "1h" => TimeSpan.FromHours(1),
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            "30d" => TimeSpan.FromDays(30),
            _ => TimeSpan.MinValue
        };

        if (window == TimeSpan.MinValue)
        {
            error = "period must be one of: 15m, 1h, 24h, 7d, 30d.";
            return false;
        }

        to ??= now;
        from ??= to.Value.Subtract(window);
        return true;
    }

    private static string EncodeCursor(DateTime createdAtUtc, Guid id)
    {
        var payload = $"{createdAtUtc.Ticks}|{id:N}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));
    }

    private static bool TryDecodeCursor(string? cursor, out DateTime? createdAtUtc, out Guid? id)
    {
        createdAtUtc = null;
        id = null;

        if (string.IsNullOrWhiteSpace(cursor))
            return true;

        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                return false;

            if (!long.TryParse(parts[0], out var ticks))
                return false;

            if (!Guid.TryParseExact(parts[1], "N", out var parsedId))
                return false;

            createdAtUtc = new DateTime(ticks, DateTimeKind.Utc);
            id = parsedId;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public record CreateLogRequest(
    Guid? ClientId,
    Guid? SiteId,
    Guid? AgentId,
    LogType Type,
    CoreLogLevel Level,
    LogSource Source,
    string Message,
    JsonElement? DataJson);
