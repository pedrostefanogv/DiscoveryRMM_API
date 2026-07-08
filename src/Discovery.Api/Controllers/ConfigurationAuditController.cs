using Discovery.Api.Filters;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/configuration-audit")]
public class ConfigurationAuditController(IConfigurationAuditService auditService) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetRecentChanges([FromQuery] int days = 90, [FromQuery] int limit = 1000)
    {
        var changes = await auditService.GetRecentChangesAsync(days, limit);
        return Ok(changes);
    }

    [HttpGet("{entityType}/{entityId:guid}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetEntityHistory(string entityType, Guid entityId, [FromQuery] int limit = 100)
    {
        var history = await auditService.GetEntityHistoryAsync(entityType, entityId, limit);
        return Ok(history);
    }

    [HttpGet("{entityType}/{entityId:guid}/field/{fieldName}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetFieldHistory(string entityType, Guid entityId, string fieldName)
    {
        var history = await auditService.GetFieldHistoryAsync(entityType, entityId, fieldName);
        return Ok(history);
    }

    [HttpGet("by-user/{username}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetChangesByUser(string username, [FromQuery] int limit = 100)
    {
        var result = await auditService.GetChangesByUserAsync(username, limit);
        return Ok(result);
    }

    [HttpGet("report")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetAuditReport([FromQuery] DateTime startDate, [FromQuery] DateTime endDate)
    {
        var report = await auditService.GetAuditReportAsync(startDate, endDate);
        return Ok(report);
    }
}
