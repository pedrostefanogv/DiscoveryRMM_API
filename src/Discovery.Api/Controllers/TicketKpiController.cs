using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/tickets/kpi")]
public class TicketKpiController : ControllerBase
{
    private readonly ITicketRepository _repo;

    public TicketKpiController(ITicketRepository repo) => _repo = repo;

    /// <summary>
    /// KPIs operacionais do módulo de tickets.
    /// Parâmetros de filtro opcionais: clientId, departmentId, since (ISO 8601).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetKpi(
        [FromQuery] Guid? clientId,
        [FromQuery] Guid? departmentId,
        [FromQuery] DateTime? since,
        CancellationToken ct)
    {
        var result = await _repo.GetKpiAsync(clientId, departmentId, since);
        return Ok(result);
    }
}
