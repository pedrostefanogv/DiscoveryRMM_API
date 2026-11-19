using Discovery.Api.Filters;
using Discovery.Core.Cqrs.MonitoringEvents.Queries;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/monitoring-events")]
public class MonitoringEventsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.Logs, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll(
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] DiscoveryDbContext db,
        [FromQuery] Guid? agentId = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Logs, ActionType.View);
        if (!access.HasGlobalAccess)
        {
            if (agentId.HasValue)
            {
                var agent = await agentRepo.GetByIdAsync(agentId.Value);
                if (agent is null)
                    return NotFound(new { error = "Agent not found." });

                var site = await siteRepo.GetByIdAsync(agent.SiteId);
                if (!TenantScopePolicy.Allows(access, site?.ClientId ?? Guid.Empty, agent.SiteId))
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "Você não tem acesso a este agente." });
            }
            else if (siteId.HasValue)
            {
                var site = await siteRepo.GetByIdAsync(siteId.Value);
                if (site is null)
                    return NotFound(new { error = "Site not found." });

                if (!TenantScopePolicy.Allows(access, site.ClientId, siteId.Value))
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "Você não tem acesso a este site." });
            }
            else if (clientId.HasValue)
            {
                if (!access.AllowedClientIds.Contains(clientId.Value))
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "Você não tem acesso a este cliente." });
            }
            else
            {
                var clampedLimit = Math.Clamp(limit, 1, 200);
                var allowedClients = access.AllowedClientIds;
                var allowedSites = access.AllowedSiteIds;
                var query = db.AgentMonitoringEvents.AsNoTracking()
                    .Where(e => db.Agents.Any(ag => ag.Id == e.AgentId &&
                        (allowedSites.Contains(ag.SiteId) ||
                         db.Sites.Any(s => s.Id == ag.SiteId && allowedClients.Contains(s.ClientId)))));

                if (CursorPaginationHelper.TryDecodeCreatedAtCursor(cursor, out var cursorTime, out var cursorId))
                    query = query.Where(e => e.CreatedAt < cursorTime || (e.CreatedAt == cursorTime && e.Id.CompareTo(cursorId) < 0));

                var events = await query
                    .OrderByDescending(e => e.CreatedAt)
                    .ThenByDescending(e => e.Id)
                    .Take(clampedLimit)
                    .Select(e => new MonitoringEventDto(
                        e.Id,
                        e.AgentId,
                        e.AlertCode,
                        e.Severity.ToString(),
                        e.Source.ToString(),
                        e.CreatedAt,
                        e.LabelsSnapshotJson))
                    .ToListAsync(ct);

                return Ok(events);
            }
        }

        var result = await mediator.Send(new ListMonitoringEventsQuery(agentId, clientId, siteId, cursor, limit), ct);
        return result.ToActionResult();
    }
}
