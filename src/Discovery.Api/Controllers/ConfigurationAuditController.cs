using Discovery.Api.Filters;
using Discovery.Core.Cqrs.ConfigurationAudit.Queries;
using Discovery.Core.Enums.Identity;
using MediatR;
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
}
