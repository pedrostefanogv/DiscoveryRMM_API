using Discovery.Api.Filters;
using Discovery.Core.Cqrs.ConfigurationAudit.Queries;
using Discovery.Infrastructure.Services;
using Discovery.Core.Enums.Identity;
using MediatR;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/configuration-audit")]
public class ConfigurationAuditController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetRecentChanges([FromQuery] int days = 90, [FromQuery] int limit = 1000)
    {
        var result = await mediator.Send(new GetRecentAuditChangesQuery(days, limit));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("{entityType}/{entityId:guid}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetEntityHistory(string entityType, Guid entityId, [FromQuery] int limit = 100)
    {
        var result = await mediator.Send(new GetEntityAuditHistoryQuery(entityType, entityId, limit));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("{entityType}/{entityId:guid}/field/{fieldName}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetFieldHistory(string entityType, Guid entityId, string fieldName)
    {
        var result = await mediator.Send(new GetFieldAuditHistoryQuery(entityType, entityId, fieldName));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("by-user/{username}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetChangesByUser(string username, [FromQuery] int limit = 100)
    {
        var result = await mediator.Send(new GetAuditChangesByUserQuery(username, limit));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("report")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetAuditReport([FromQuery] DateTime startDate, [FromQuery] DateTime endDate)
    {
        var result = await mediator.Send(new GetAuditReportQuery(startDate, endDate));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── Hash chain (M241) ─────────────────────────────────────────────────

    /// <summary>
    /// Verifica a hash chain das entradas de auditoria no período, recomputando
    /// os hashes encadeados. Entradas fora da chain (legado/fail-open) são ignoradas.
    /// </summary>
    [HttpGet("chain/verify")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> VerifyChain([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var result = await mediator.Send(new VerifyAuditChainQuery(from, to));
        return result.Match<IActionResult>(success: Ok, failure: BadRequest);
    }

    /// <summary>
    /// Exporta as entradas encadeadas no período. format=json (default) ou csv.
    /// </summary>
    [HttpGet("chain/export")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> ExportChain(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string format = "json")
    {
        var result = await mediator.Send(new ExportAuditChainQuery(from, to, format));
        return result.Match<IActionResult>(
            success: export => Content(export.Content, export.Format == "csv" ? "text/csv" : "application/json"),
            failure: BadRequest);
    }
}
