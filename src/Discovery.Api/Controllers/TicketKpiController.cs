using Discovery.Api.Filters;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/tickets/kpi")]
public class TicketKpiController : ControllerBase
{
    private readonly ITicketRepository _repo;
    private readonly ITicketKpiCacheService _kpiCache;

    public TicketKpiController(ITicketRepository repo, ITicketKpiCacheService kpiCache)
    {
        _repo = repo;
        _kpiCache = kpiCache;
    }

    /// <summary>
    /// KPIs operacionais do módulo de tickets.
    /// Cache de 60 segundos com invalidação on-write via Redis.
    /// Parâmetros de filtro opcionais: clientId, departmentId, since (ISO 8601).
    /// </summary>
    [HttpGet]
    [RequirePermission(ResourceType.Dashboard, ActionType.View)]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, NoStore = false)]
    public async Task<IActionResult> GetKpi(
        [FromQuery] Guid? clientId,
        [FromQuery] Guid? departmentId,
        [FromQuery] DateTime? since,
        CancellationToken ct)
    {
        var result = await _kpiCache.GetOrComputeAsync(
            clientId,
            departmentId,
            since,
            () => _repo.GetKpiAsync(clientId, departmentId, since),
            ct);

        return Ok(result);
    }
}
