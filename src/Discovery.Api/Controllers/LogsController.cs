using Discovery.Core.Cqrs.Logs.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/logs")]
public class LogsController(IMediator mediator) : ControllerBase
{
    /// <summary>
    /// Lista paginada de logs (legado) — compatível com chamadas sem subpath.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? agentId = null,
        [FromQuery] string? siteId = null,
        [FromQuery] string? clientId = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new ListLogsQuery(agentId, siteId, clientId, cursor, limit));
        return result.ToActionResult();
    }

    /// <summary>
    /// Lista paginada de logs com suporte a filtros expandidos e paginação por cursor.
    /// </summary>
    [HttpGet("page")]
    public async Task<IActionResult> GetPage(
        [FromQuery] string? agentId = null,
        [FromQuery] string? siteId = null,
        [FromQuery] string? clientId = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        [FromQuery] int? level = null,
        [FromQuery] int? type = null,
        [FromQuery] int? source = null,
        [FromQuery] string? period = null,
        [FromQuery] string? search = null)
    {
        var result = await mediator.Send(new ListLogsQuery(
            agentId, siteId, clientId, cursor, limit, level, type, source, period, search));
        return result.ToActionResult();
    }

    /// <summary>
    /// Sumário agregado de logs: total, contagens por nível/tipo/origem/escopo.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] string? agentId = null,
        [FromQuery] string? siteId = null,
        [FromQuery] string? clientId = null,
        [FromQuery] int? level = null,
        [FromQuery] int? type = null,
        [FromQuery] int? source = null,
        [FromQuery] string? period = null,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new GetLogsSummaryQuery(
            agentId, siteId, clientId, level, type, source, period, search, limit));
        return result.ToActionResult();
    }

    /// <summary>
    /// Opções de escopo disponíveis para filtro de logs (clientes, sites, agents com registros).
    /// </summary>
    [HttpGet("scope-options")]
    public async Task<IActionResult> GetScopeOptions()
    {
        var result = await mediator.Send(new GetLogsScopeOptionsQuery());
        return result.ToActionResult();
    }
}
